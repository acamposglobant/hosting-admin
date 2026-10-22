namespace HostingAdmin.Infrastructure.Linux;

/// <summary>A raw CPU sample from /proc/stat, in jiffies.</summary>
public sealed record CpuSample(long IdleJiffies, long TotalJiffies);

/// <summary>
/// Defensive parser for /proc/stat (plan §3.6): fixed, known-safe file; malformed
/// lines are skipped rather than throwing. CPU usage is derived from the delta
/// of two samples.
/// </summary>
public static class ProcStatParser
{
    /// <summary>
    /// Parses the aggregate "cpu" line into a sample. Returns null when the
    /// line is missing or malformed.
    /// </summary>
    public static CpuSample? ParseAggregateCpu(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                continue;
            }

            return ParseCpuLine(line);
        }

        return null;
    }

    /// <summary>Counts "cpuN" lines (per-core statistics), excluding the aggregate line.</summary>
    public static int? ParseCoreCount(string content)
    {
        var cores = 0;
        foreach (var line in content.Split('\n'))
        {
            if (line.StartsWith("cpu", StringComparison.Ordinal) &&
                line.Length > 3 &&
                char.IsAsciiDigit(line[3]))
            {
                cores++;
            }
        }

        return cores > 0 ? cores : null;
    }

    /// <summary>
    /// Computes CPU usage percent between two samples; null when total jiffies
    /// did not advance (no measurable interval).
    /// </summary>
    public static double? ComputeUsagePercent(CpuSample earlier, CpuSample later)
    {
        var totalDelta = later.TotalJiffies - earlier.TotalJiffies;
        if (totalDelta <= 0)
        {
            return null;
        }

        var idleDelta = later.IdleJiffies - earlier.IdleJiffies;
        var busy = totalDelta - idleDelta;
        return 100.0 * busy / totalDelta;
    }

    private static CpuSample? ParseCpuLine(string line)
    {
        // Format: "cpu  user nice system idle iowait irq softirq steal ..."
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
        {
            return null;
        }

        if (!TryParseLong(fields[1], out var user) ||
            !TryParseLong(fields[2], out var nice) ||
            !TryParseLong(fields[3], out var system) ||
            !TryParseLong(fields[4], out var idle))
        {
            return null;
        }

        long total = user + nice + system + idle;
        for (var index = 5; index < fields.Length; index++)
        {
            if (TryParseLong(fields[index], out var extra))
            {
                total += extra;
            }
        }

        return new CpuSample(IdleJiffies: idle, TotalJiffies: total);
    }

    private static bool TryParseLong(string text, out long value) =>
        long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out value);
}
