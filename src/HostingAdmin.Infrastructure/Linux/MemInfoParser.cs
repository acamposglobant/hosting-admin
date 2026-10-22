namespace HostingAdmin.Infrastructure.Linux;

/// <summary>Memory readings in bytes.</summary>
public sealed record MemoryInfo(long TotalBytes, long AvailableBytes, long UsedBytes);

/// <summary>
/// Defensive parser for /proc/meminfo (plan §3.6). Values are reported in kB
/// by the kernel; converted to bytes here. Malformed content yields null.
/// </summary>
public static class MemInfoParser
{
    public static MemoryInfo? Parse(string content)
    {
        long? totalKb = null;
        long? availableKb = null;

        foreach (var line in content.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var valuePart = line[(separator + 1)..].Trim();
            var space = valuePart.IndexOf(' ');
            var numberText = space > 0 ? valuePart[..space] : valuePart;

            if (!long.TryParse(numberText, System.Globalization.CultureInfo.InvariantCulture, out var kilobytes))
            {
                continue;
            }

            if (name == "MemTotal")
            {
                totalKb = kilobytes;
            }
            else if (name == "MemAvailable")
            {
                availableKb = kilobytes;
            }
        }

        if (totalKb is null)
        {
            return null;
        }

        var available = availableKb ?? 0;
        var total = totalKb.Value * 1024L;
        var availableBytes = available * 1024L;
        var used = Math.Max(0, total - availableBytes);
        return new MemoryInfo(
            TotalBytes: total,
            AvailableBytes: availableBytes,
            UsedBytes: used);
    }
}
