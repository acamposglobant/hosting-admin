using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingAdmin.Worker;

/// <summary>
/// Ships audit events to the local syslog so an off-host copy becomes possible
/// via the operator's rsyslog forwarding config (increment 3, D26).
///
/// Behavior:
/// - Config-gated: when <see cref="AuditShippingOptions.Enabled"/> is false
///   (the default) the worker exits immediately — zero syslog writes, zero
///   polling (TA19).
/// - Polls audit_events for rows newer than the persisted cursor
///   (admin_state key 'audit.shipping.last_id') and writes one syslog line per
///   event, in id order (TA15).
/// - Failure behavior: a sink failure is logged as a warning and retried next
///   poll; the cursor is not advanced past unsent rows, audit rows are never
///   mutated, and the worker never crashes the host (TA16).
/// - Shipping is best-effort duplication, not a transactional guarantee (D26).
/// </summary>
public sealed class AuditShippingWorker(
    IAuditStore auditStore,
    IAuditShippingCursorStore cursorStore,
    ISyslogWriter syslogWriter,
    AuditShippingOptions options,
    ILogger<AuditShippingWorker> logger) : BackgroundService
{
    private readonly IAuditStore _auditStore = auditStore;
    private readonly IAuditShippingCursorStore _cursorStore = cursorStore;
    private readonly ISyslogWriter _syslogWriter = syslogWriter;
    private readonly AuditShippingOptions _options = options;
    private readonly ILogger<AuditShippingWorker> _logger = logger;

    private long _cursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Audit shipping is disabled (AuditShipping:Enabled=false); worker idle.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        var batchSize = Math.Max(1, _options.BatchSize);

        _logger.LogInformation(
            "Audit shipping enabled: polling every {Interval}s, batch {Batch}, sink {Sink}",
            interval.TotalSeconds, batchSize, _syslogWriter.GetType().Name);

        // Resume from the persisted cursor: no duplication, no loss (TA15).
        _cursor = await _cursorStore.GetLastShippedIdAsync(stoppingToken);

        // Ship immediately on startup (drain backlog / resume), then poll.
        await ShipPendingAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested
               && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ShipPendingAsync(stoppingToken);
        }
    }

    private async Task ShipPendingAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var rows = await _auditStore.QueryAfterIdAsync(_cursor, _options.BatchSize, stoppingToken);
                if (rows.Count == 0)
                {
                    return;
                }

                foreach (var row in rows)
                {
                    var line = SyslogLineFormatter.Format(row.Id, row.Event);
                    await _syslogWriter.WriteAsync(line, stoppingToken);
                    _cursor = row.Id;
                }

                // Persist the cursor only after the whole batch was written:
                // a mid-batch failure retries the unsent tail next poll (TA16).
                await _cursorStore.SetLastShippedIdAsync(_cursor, stoppingToken);

                if (rows.Count < _options.BatchSize)
                {
                    return; // drained
                }
                // Full batch: loop again immediately to drain the backlog.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: cursor already persisted per batch; resume on next start.
        }
        catch (Exception ex)
        {
            // Sink or store failure is non-fatal (TA16): warn, keep the cursor
            // at the last successfully shipped id, retry next poll. Audit rows
            // are never mutated by this worker.
            _logger.LogWarning(ex,
                "Audit shipping failed at cursor {Cursor}; will retry next poll (best-effort, D26)",
                _cursor);
        }
    }
}
