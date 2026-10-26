using System.Net.Sockets;

namespace HostingAdmin.Worker;

/// <summary>
/// Writes syslog lines to the local syslog socket (/dev/log, RFC 3164/5424
/// text lines — increment 3, D26). No TCP, no network listener, no shell:
/// a unix datagram socket send per line. Remote forwarding to a log host is
/// the operator's rsyslog configuration, not this process's concern.
/// </summary>
public sealed class DevLogSyslogWriter : ISyslogWriter
{
    public const string DefaultSocketPath = "/dev/log";

    private readonly string _socketPath;

    public DevLogSyslogWriter() : this(DefaultSocketPath)
    {
    }

    /// <summary>Socket path is injectable for tests (journald's dev-log, tmp paths).</summary>
    public DevLogSyslogWriter(string socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            throw new ArgumentException("Syslog socket path must be configured.", nameof(socketPath));
        }

        _socketPath = socketPath;
    }

    public async Task WriteAsync(string line, CancellationToken cancellationToken = default)
    {
        // One datagram per line: syslog datagram transport is line-framed, and
        // the formatter guarantees the line contains no raw newline (TA15).
        var payload = System.Text.Encoding.UTF8.GetBytes(line);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken);
        await socket.SendAsync(payload, SocketFlags.None, cancellationToken);
    }
}
