namespace HostingAdmin.Core.Auth;

/// <summary>
/// Persisted login throttle state for the single admin account. Kept in the
/// database so lockouts survive process restarts.
/// </summary>
public sealed record LoginThrottleState
{
    public int FailedCount { get; init; }

    /// <summary>Start of the current failure-counting window (UTC).</summary>
    public DateTime? WindowStartUtc { get; init; }

    /// <summary>Until when login is locked, or null when not locked.</summary>
    public DateTime? LockedUntilUtc { get; init; }

    public static LoginThrottleState Empty { get; } = new();
}

/// <summary>
/// Persistence for login rate-limiting state (plan task 6: 429 after N attempts).
/// Implementations must use parameterized queries.
/// </summary>
public interface ILoginThrottleStore
{
    Task<LoginThrottleState> GetStateAsync(CancellationToken cancellationToken = default);

    Task SetStateAsync(LoginThrottleState state, CancellationToken cancellationToken = default);
}
