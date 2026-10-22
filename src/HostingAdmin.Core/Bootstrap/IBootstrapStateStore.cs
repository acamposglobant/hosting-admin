namespace HostingAdmin.Core.Bootstrap;

/// <summary>
/// Persists the bootstrap lifecycle state (plan D6) in the admin_state table.
/// Completion is durable: once <see cref="BootstrapStatus.Completed"/> is stored,
/// bootstrap can never be reopened through any API request — only a fresh
/// deployment (empty database) starts unconfigured again.
/// </summary>
public interface IBootstrapStateStore
{
    Task<BootstrapState> GetStateAsync(CancellationToken cancellationToken = default);

    Task SetStateAsync(BootstrapState state, CancellationToken cancellationToken = default);
}
