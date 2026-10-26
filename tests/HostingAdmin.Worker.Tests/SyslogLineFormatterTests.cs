using System.Text.RegularExpressions;
using HostingAdmin.Core.Audit;
using HostingAdmin.Worker;
using Xunit;

namespace HostingAdmin.Worker.Tests;

/// <summary>
/// TA17: the shipped line is exactly one line — JSON-escaped payload, fixed
/// priority/tag prefix, no raw newlines even for hostile detail content, and
/// no forged priority prefix inside the payload.
/// </summary>
public class SyslogLineFormatterTests
{
    private static AuditEvent MakeEvent(string? detail = null, string action = "auth.login") => new()
    {
        TimestampUtc = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc),
        Actor = "admin",
        Action = action,
        Outcome = AuditOutcomes.Success,
        SourceIp = "127.0.0.1",
        Detail = detail,
    };

    [Fact]
    public void Format_ProducesSingleLine_WithFixedPrefix()
    {
        var line = SyslogLineFormatter.Format(42, MakeEvent(detail: "plain detail"));

        Assert.StartsWith("<13>hostingadmin-audit: ", line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("\"id\":42", line);
        Assert.Contains("\"action\":\"auth.login\"", line);
    }

    [Fact]
    public void Format_NewlinesInDetail_AreEscaped_NotRaw()
    {
        var line = SyslogLineFormatter.Format(1, MakeEvent(detail: "line1\nline2\r\ninjected\u2028end"));

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        // The newline survives only as a JSON escape sequence.
        Assert.Contains("\\n", line);
    }

    [Fact]
    public void Format_ForgedPriorityInDetail_IsEscapedNotInterpreted()
    {
        var line = SyslogLineFormatter.Format(2, MakeEvent(
            detail: "<13>hostingadmin-audit: {\"actor\":\"attacker\"}"));

        // Exactly one priority prefix: the real one at position 0.
        Assert.Single(Regex.Matches(line, "<13>"));
        Assert.StartsWith("<13>hostingadmin-audit: ", line);
    }

    [Fact]
    public void Format_ControlCharacters_AreEscaped()
    {
        var line = SyslogLineFormatter.Format(3, MakeEvent(detail: "a\u0000b\u001fc"));

        Assert.DoesNotContain('\u0000', line);
        Assert.DoesNotContain('\u001f', line);
    }

    [Fact]
    public void Format_NullDetail_IsNullInPayload()
    {
        var line = SyslogLineFormatter.Format(4, MakeEvent(detail: null));

        Assert.Contains("\"detail\":null", line);
        Assert.DoesNotContain('\n', line);
    }
}
