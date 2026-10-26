using System.Text.Json;
using System.Text.Json.Serialization;
using HostingAdmin.Core.Audit;

namespace HostingAdmin.Worker;

/// <summary>
/// Formats one RFC 3164/5424-style syslog line per audit event (increment 3,
/// D26). The payload is a fixed JSON structure serialized with
/// <see cref="JsonSerializer"/> — never string-built from raw input — so
/// newlines and control characters in detail fields are escaped by the JSON
/// encoder and line-framing injection into syslog is not possible (TA15/TA17).
/// </summary>
public static class SyslogLineFormatter
{
    public const string Tag = "hostingadmin-audit";

    /// <summary>
    /// Priority &lt;13&gt; = facility user (1) &lt;&lt; 3 + severity notice (5).
    /// Fixed: no part of the priority line is derived from event content.
    /// </summary>
    public const int Priority = 13;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string Prefix = $"<{Priority}>{Tag}: ";

    /// <summary>JSON payload shape (fixed structure; no dynamic keys).</summary>
    private sealed record ShippedEvent(
        long id,
        DateTime timestampUtc,
        string actor,
        string action,
        string outcome,
        string? sourceIp,
        string? detail);

    /// <summary>
    /// Formats one audit row as exactly one syslog line. The JSON encoder
    /// escapes newlines inside string values, so the result never contains a
    /// raw line break — one event, one line, regardless of detail content.
    /// </summary>
    public static string Format(long id, AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var payload = JsonSerializer.Serialize(
            new ShippedEvent(
                id,
                auditEvent.TimestampUtc,
                auditEvent.Actor,
                auditEvent.Action,
                auditEvent.Outcome,
                auditEvent.SourceIp,
                auditEvent.Detail),
            JsonOptions);

        return Prefix + payload;
    }
}
