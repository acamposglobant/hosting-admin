namespace HostingAdmin.Core.Services;

/// <summary>
/// Persistence for pending destructive service-action confirmations (plan
/// §3.4/§3.5, D20). Implementations store only the token hash, never the raw
/// token (D25). All lookups by raw token hash the input first.
/// </summary>
public interface IServiceActionConfirmationStore
{
    /// <summary>Inserts a new confirmation row.</summary>
    Task InsertAsync(ServiceActionConfirmation confirmation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the confirmation with the given token hash, or null when unknown.
    /// </summary>
    Task<ServiceActionConfirmation?> GetByIdHashAsync(string idHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the confirmation consumed (single-use: on confirm and on failed
    /// execution). Returns false when the row was already consumed or is gone.
    /// </summary>
    Task<bool> TryConsumeAsync(string idHash, CancellationToken cancellationToken = default);

    /// <summary>Deletes the confirmation row (cancel path). Idempotent.</summary>
    Task DeleteAsync(string idHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes expired confirmations. Returns the number of rows removed.
    /// Called by the periodic maintenance job (plan §3.4).
    /// </summary>
    Task<int> PruneExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
