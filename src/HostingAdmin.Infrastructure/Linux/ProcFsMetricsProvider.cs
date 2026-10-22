using HostingAdmin.Core.Metrics;

namespace HostingAdmin.Infrastructure.Linux;

/// <summary>
/// Reads metrics from fixed, known-safe procfs files (plan §3.6, D11): /proc/stat,
/// /proc/meminfo, /proc/swaps, /proc/loadavg, /proc/uptime. Never enumerates
/// processes and never spawns processes. Each file is read defensively: a missing
/// or unreadable file degrades that section to null while the rest of the
/// snapshot is still served. CPU usage requires two samples; the first snapshot
/// reports usagePercent as null.
/// </summary>
public sealed class ProcFsMetricsProvider : IMetricsProvider
{
    private const string ProcStatPath = "/proc/stat";
    private const string ProcMemInfoPath = "/proc/meminfo";
    private const string ProcSwapsPath = "/proc/swaps";
    private const string ProcLoadAvgPath = "/proc/loadavg";
    private const string ProcUptimePath = "/proc/uptime";

    private readonly object sampleLock = new();
    private CpuSample? lastCpuSample;

    public async Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var sampledAt = DateTime.UtcNow;

        var statContent = await ReadFileAsync(ProcStatPath, cancellationToken);
        var memInfoContent = await ReadFileAsync(ProcMemInfoPath, cancellationToken);
        var swapsContent = await ReadFileAsync(ProcSwapsPath, cancellationToken);
        var loadAvgContent = await ReadFileAsync(ProcLoadAvgPath, cancellationToken);
        var uptimeContent = await ReadFileAsync(ProcUptimePath, cancellationToken);

        if (statContent is null && memInfoContent is null && swapsContent is null)
        {
            return MetricsSnapshot.Unavailable("procfs_unavailable");
        }

        // CPU: usage requires a delta between two samples (D11 runtime detection).
        var currentSample = statContent is null ? null : ProcStatParser.ParseAggregateCpu(statContent);
        int? cores = statContent is null ? null : ProcStatParser.ParseCoreCount(statContent);
        double? usagePercent = null;
        lock (sampleLock)
        {
            if (lastCpuSample is not null && currentSample is not null)
            {
                usagePercent = ProcStatParser.ComputeUsagePercent(lastCpuSample, currentSample);
            }

            lastCpuSample = currentSample ?? lastCpuSample;
        }

        var load = loadAvgContent is null
            ? (Load1m: (double?)null, Load5m: (double?)null, Load15m: (double?)null)
            : LoadAvgParser.ParseLoadAverages(loadAvgContent);

        var memory = memInfoContent is null ? null : MemInfoParser.Parse(memInfoContent);
        var swap = swapsContent is null ? null : SwapsParser.Parse(swapsContent);
        var uptime = uptimeContent is null ? null : LoadAvgParser.ParseUptimeSeconds(uptimeContent);

        var cpu = new CpuMetrics(
            UsagePercent: usagePercent,
            Cores: cores,
            LoadAverage1m: load.Load1m,
            LoadAverage5m: load.Load5m,
            LoadAverage15m: load.Load15m,
            SampledAt: sampledAt);

        var memoryMetrics = memory is null
            ? null
            : new MemoryMetrics(
                TotalBytes: memory.TotalBytes,
                UsedBytes: memory.UsedBytes,
                AvailableBytes: memory.AvailableBytes,
                UsagePercent: memory.TotalBytes > 0
                    ? 100.0 * memory.UsedBytes / memory.TotalBytes
                    : null);

        var swapMetrics = swap is null
            ? null
            : new SwapMetrics(
                TotalBytes: swap.TotalBytes,
                UsedBytes: swap.UsedBytes,
                UsagePercent: swap.TotalBytes > 0 ? 100.0 * swap.UsedBytes / swap.TotalBytes : null);

        return new MetricsSnapshot(
            IsAvailable: true,
            UnavailableReason: null,
            Cpu: cpu,
            Memory: memoryMetrics,
            Swap: swapMetrics,
            UptimeSeconds: uptime);
    }

    private static async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FileNotFoundException
                or DirectoryNotFoundException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
