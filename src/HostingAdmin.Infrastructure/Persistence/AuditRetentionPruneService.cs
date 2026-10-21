using HostingAdmin.Core.Audit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// Periodic maintenance job: prunes audit events older than the configured
/// retention window (D9: default 90 days). Session cleanup joins this same
/// maintenance path in task 5.
/// </summary>
public sealed class AuditRetentionPruneService(
    IAuditStore auditStore,
    AuditOptions auditOptions,
    ILogger<AuditRetentionPruneService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, auditOptions.PruneIntervalHours));

        using var timer = new PeriodicTimer(interval);

        logger.LogInformation(
            "Audit retention prune scheduled every {Hours}h (retention {Days} days)",
            interval.TotalHours, auditOptions.RetentionDays);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-auditOptions.RetentionDays);
                await auditStore.PruneOlderThanAsync(cutoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Prune failure must not crash the host; it is retried next interval.
                logger.LogError(ex, "Audit retention prune failed; will retry next interval");
            }
        }
    }
}
