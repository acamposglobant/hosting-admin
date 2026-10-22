using HostingAdmin.Infrastructure.Linux;
using Xunit;

namespace HostingAdmin.Infrastructure.Tests;

/// <summary>
/// Fixture tests for the procfs parsers (plan task 10, D12): realistic /proc
/// content shapes; malformed lines are skipped, never thrown (§3.6).
/// </summary>
public sealed class ProcFsParserTests
{
    // --- /proc/stat ---

    private const string StatContent = """
        cpu  74608 2520 24433 1117073 6176 4054 0 0 0 0
        cpu0 37304 1260 12216 558536 3088 2027 0 0 0 0
        cpu1 37304 1260 12217 558536 3088 2028 0 0 0 0
        intr 1134567 ...
        """;

    [Fact]
    public void ParseAggregateCpu_ComputesIdleAndTotal()
    {
        var sample = ProcStatParser.ParseAggregateCpu(StatContent);

        Assert.NotNull(sample);
        // user 74608 + nice 2520 + system 24433 + idle 1117073 + iowait 6176
        // + irq 4054 + softirq 0 + steal 0
        Assert.Equal(1117073, sample!.IdleJiffies);
        Assert.Equal(74608 + 2520 + 24433 + 1117073 + 6176 + 4054, sample.TotalJiffies);
    }

    [Fact]
    public void ParseAggregateCpu_MalformedLine_ReturnsNull()
    {
        Assert.Null(ProcStatParser.ParseAggregateCpu("cpu 1 2 3"));
        Assert.Null(ProcStatParser.ParseAggregateCpu("cpu x y z w"));
        Assert.Null(ProcStatParser.ParseAggregateCpu(""));
    }

    [Fact]
    public void ParseCoreCount_CountsPerCoreLines()
    {
        Assert.Equal(2, ProcStatParser.ParseCoreCount(StatContent));
        Assert.Null(ProcStatParser.ParseCoreCount("cpu  1 2 3 4"));
    }

    [Fact]
    public void ComputeUsagePercent_KnownDelta_GivesExpectedValue()
    {
        // 100 jiffies advanced, 80 idle → 20% busy.
        var earlier = new CpuSample(IdleJiffies: 900, TotalJiffies: 1000);
        var later = new CpuSample(IdleJiffies: 980, TotalJiffies: 1100);

        var usage = ProcStatParser.ComputeUsagePercent(earlier, later);

        Assert.Equal(20.0, usage!.Value, precision: 1);
    }

    [Fact]
    public void ComputeUsagePercent_NoAdvance_ReturnsNull()
    {
        var sample = new CpuSample(100, 1000);

        Assert.Null(ProcStatParser.ComputeUsagePercent(sample, sample));
    }

    // --- /proc/meminfo ---

    [Fact]
    public void MemInfoParser_ParsesTotalAndAvailable()
    {
        const string content = """
            MemTotal:        2048000 kB
            MemFree:          512000 kB
            MemAvailable:    1024000 kB
            Buffers:           64000 kB
            Cached:           256000 kB
            """;

        var info = MemInfoParser.Parse(content);

        Assert.NotNull(info);
        Assert.Equal(2048000L * 1024, info!.TotalBytes);
        Assert.Equal(1024000L * 1024, info.AvailableBytes);
        Assert.Equal(2048000L * 1024 - 1024000L * 1024, info.UsedBytes);
    }

    [Fact]
    public void MemInfoParser_MissingTotal_ReturnsNull()
    {
        Assert.Null(MemInfoParser.Parse("MemFree: 100 kB\n"));
        Assert.Null(MemInfoParser.Parse("garbage"));
    }

    // --- /proc/swaps ---

    [Fact]
    public void SwapsParser_SumsRows()
    {
        const string content = """
            Filename				Type		Size	Used	Priority
            /swapfile                              	file    	524287	0	-2
            /dev/sda2                              	partition	1048576	204800	-2
            """;

        var swap = SwapsParser.Parse(content);

        Assert.NotNull(swap);
        Assert.Equal((524287L + 1048576L) * 1024, swap!.TotalBytes);
        Assert.Equal(204800L * 1024, swap.UsedBytes);
    }

    [Fact]
    public void SwapsParser_HeaderOnly_ReturnsNull()
    {
        const string content = "Filename\t\t\tType\t\tSize\t\tUsed\t\tPriority\n";

        Assert.Null(SwapsParser.Parse(content));
    }

    // --- /proc/loadavg and /proc/uptime ---

    [Fact]
    public void LoadAvgParser_ParsesThreeAverages()
    {
        var (load1, load5, load15) = LoadAvgParser.ParseLoadAverages("0.31 0.22 0.18 1/812 34567");

        Assert.Equal(0.31, load1);
        Assert.Equal(0.22, load5);
        Assert.Equal(0.18, load15);
    }

    [Fact]
    public void LoadAvgParser_Malformed_ReturnsNulls()
    {
        var (load1, load5, load15) = LoadAvgParser.ParseLoadAverages("nonsense");

        Assert.Null(load1);
        Assert.Null(load5);
        Assert.Null(load15);
    }

    [Fact]
    public void UptimeParser_ParsesFirstField()
    {
        Assert.Equal(864000.42, LoadAvgParser.ParseUptimeSeconds("864000.42 1234567.89"));
        Assert.Null(LoadAvgParser.ParseUptimeSeconds("not-a-number"));
        Assert.Null(LoadAvgParser.ParseUptimeSeconds(""));
    }
}
