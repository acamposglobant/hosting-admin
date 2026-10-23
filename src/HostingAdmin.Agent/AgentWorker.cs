using System.Runtime.InteropServices;

namespace HostingAdmin.Agent;

/// <summary>
/// BackgroundService wrapper around <see cref="AgentSocketListener"/>. On
/// non-Linux hosts the worker idles (the agent is not run on Windows dev, D17);
/// the process exits with a clear message from Program.cs.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly AgentSocketListener _listener;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(AgentSocketListener listener, ILogger<AgentWorker> logger)
    {
        _listener = listener;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _logger.LogWarning("Agent worker is idle: unix sockets require Linux (D17).");
            return;
        }

        try
        {
            await _listener.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Agent shutting down");
        }
    }
}
