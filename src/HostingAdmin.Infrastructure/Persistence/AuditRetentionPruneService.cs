using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Sessions;
using HostingAdmin.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostingAdmin.Infrastructure.Persistence;

/// <summary>
/// Periodic maintenance job: prunes audit events older than the configured
/// retention window (D9: default 90 days), expired/revoked sessions
/// (plan §3.5 cleanup, T10), and expired service-action confirmations
/// (plan §3.4). All cleanup joins this same maintenance path.
/// </summary>
public sealed class AuditRetentionPruneService(
    IAuditStore auditStore,
    ISessionStore sessionStore,
    IServiceActionConfirmationStore confirmationStore,
    AuditOptions auditOptions,
    ILogger<AuditRetentionPruneService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, auditOptions.PruneIntervalHours));

        using var timer = new PeriodicTimer(interval);

        logger.LogInformation(
            "Maintenance prune scheduled every {Hours}h (audit retention {Days} days)",
            interval.TotalHours, auditOptions.RetentionDays);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-auditOptions.RetentionDays);
                await auditStore.PruneOlderThanAsync(cutoff, stoppingToken);

                var prunedSessions = await sessionStore.PruneExpiredAsync(DateTime.UtcNow, stoppingToken);
                if (prunedSessions > 0)
                {
                    logger.LogInformation("Session cleanup removed {Count} expired/revoked sessions", prunedSessions);
                }

                var prunedConfirmations = await confirmationStore.PruneExpiredAsync(DateTime.UtcNow, stoppingToken);
                if (prunedConfirmations > 0)
                {
                    logger.LogInformation(
                        "Confirmation cleanup removed {Count} expired service-action confirmations",
                        prunedConfirmations);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Prune failure must not crash the host; it is retried next interval.
                logger.LogError(ex, "Maintenance prune failed; will retry next interval");
            }
        }
    }
}
