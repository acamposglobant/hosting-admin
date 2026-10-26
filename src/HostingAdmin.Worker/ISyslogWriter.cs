namespace HostingAdmin.Worker;

/// <summary>
/// Sink for shipped audit lines (increment 3, D26). One call = one syslog line.
/// Implementations must not throw for transient sink failures into the worker's
/// hot path contract: the worker treats any exception as "sink unavailable",
/// logs a warning and retries next poll (TA16) — audit rows are never mutated.
/// </summary>
public interface ISyslogWriter
{
    /// <summary>Writes one pre-formatted syslog line. Throws on failure.</summary>
    Task WriteAsync(string line, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op sink used on non-Linux hosts (the agent/worker path is not exercised
/// on Windows dev) and in tests that supply their own recording writer.
/// </summary>
public sealed class NullSyslogWriter : ISyslogWriter
{
    public Task WriteAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
