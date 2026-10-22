namespace HostingAdmin.Infrastructure.Linux;

/// <summary>Load averages and uptime.</summary>
public sealed record LoadAndUptime(
    double? LoadAverage1m,
    double? LoadAverage5m,
    double? LoadAverage15m,
    double? UptimeSeconds);

/// <summary>
/// Defensive parsers for /proc/loadavg and /proc/uptime (plan §3.6): fixed,
/// known-safe files; malformed content yields null fields rather than throwing.
/// </summary>
public static class LoadAvgParser
{
    /// <summary>Parses /proc/loadavg ("0.31 0.22 0.18 1/812 34567").</summary>
    public static (double? Load1m, double? Load5m, double? Load15m) ParseLoadAverages(string content)
    {
        var fields = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3)
        {
            return (null, null, null);
        }

        return (
            ParseDouble(fields[0]),
            ParseDouble(fields[1]),
            ParseDouble(fields[2]));
    }

    /// <summary>Parses /proc/uptime ("864000.42 1234567.89") — first field is total uptime.</summary>
    public static double? ParseUptimeSeconds(string content)
    {
        var fields = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 0 ? ParseDouble(fields[0]) : null;
    }

    private static double? ParseDouble(string text) =>
        double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
