namespace HostingAdmin.Infrastructure.Linux;

/// <summary>Swap readings in bytes.</summary>
public sealed record SwapInfo(long TotalBytes, long UsedBytes);

/// <summary>
/// Defensive parser for /proc/swaps (plan §3.6): a text table whose header is
/// "Filename Type Size Used Priority" (sizes in kB). Malformed content yields null.
/// </summary>
public static class SwapsParser
{
    public static SwapInfo? Parse(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return null; // Header only (or empty): no swap configured.
        }

        long totalKb = 0;
        long usedKb = 0;

        // Skip the header line; columns: Filename Type Size Used Priority.
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 ||
                !long.TryParse(fields[2], System.Globalization.CultureInfo.InvariantCulture, out var sizeKb) ||
                !long.TryParse(fields[3], System.Globalization.CultureInfo.InvariantCulture, out var rowUsedKb))
            {
                continue;
            }

            totalKb += sizeKb;
            usedKb += rowUsedKb;
        }

        if (totalKb <= 0)
        {
            return null;
        }

        return new SwapInfo(
            TotalBytes: totalKb * 1024L,
            UsedBytes: usedKb * 1024L);
    }
}
