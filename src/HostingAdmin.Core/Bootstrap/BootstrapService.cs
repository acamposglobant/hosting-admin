using HostingAdmin.Core.Auth;

namespace HostingAdmin.Core.Bootstrap;

/// <summary>Outcome of a bootstrap activation attempt (plan D6).</summary>
public enum ActivateOutcome
{
    /// <summary>The initialization window was just opened.</summary>
    Opened,

    /// <summary>The window was already open; the existing expiry is kept (no extension).</summary>
    AlreadyOpen,

    /// <summary>Bootstrap already completed — permanently closed (T7).</summary>
    AlreadyCompleted,
}

/// <summary>Outcome of a first-admin creation attempt (plan D6, T7).</summary>
public enum CreateAdminOutcome
{
    /// <summary>Credential stored, completion persisted, window closed.</summary>
    Succeeded,

    /// <summary>Bootstrap already completed — cannot run again via any API request.</summary>
    AlreadyCompleted,

    /// <summary>No window is open (never activated or expired).</summary>
    WindowClosed,

    /// <summary>Password violates the configured length policy; window stays open.</summary>
    WeakPassword,
}

/// <summary>
/// First-admin bootstrap use case (plan §3.4, D6, task 8): a local operator
/// activates a limited initialization window, then creates the single admin
/// credential inside it. Completion is persisted and irreversible through the
/// API. Auditing is performed by the caller (it owns request context such as
/// the source IP); this service only decides outcomes and updates state.
/// The password is never stored, logged, or included in any result (D7).
/// </summary>
public sealed class BootstrapService(
    IBootstrapStateStore stateStore,
    IAdminCredentialStore credentialStore,
    BootstrapOptions options)
{
    /// <summary>
    /// Opens the limited initialization window. Re-activation while the window
    /// is open keeps the existing expiry — it never extends the window.
    /// </summary>
    public async Task<(ActivateOutcome Outcome, DateTime? WindowExpiresUtc)> ActivateAsync(
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var state = await stateStore.GetStateAsync(cancellationToken);

        if (state.Status == BootstrapStatus.Completed)
        {
            return (ActivateOutcome.AlreadyCompleted, null);
        }

        if (state.Status == BootstrapStatus.WindowOpen &&
            state.WindowExpiresUtc is { } expires && nowUtc < expires)
        {
            return (ActivateOutcome.AlreadyOpen, expires);
        }

        var newExpiry = nowUtc + options.WindowDuration;
        await stateStore.SetStateAsync(BootstrapState.OpenWindow(newExpiry), cancellationToken);
        return (ActivateOutcome.Opened, newExpiry);
    }

    /// <summary>
    /// Creates the first admin credential. Only valid while the initialization
    /// window is open and bootstrap has not completed. On success the completion
    /// is persisted and the window closes permanently (T7).
    /// </summary>
    public async Task<CreateAdminOutcome> CreateAdminAsync(
        string password, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var state = await stateStore.GetStateAsync(cancellationToken);

        if (state.Status == BootstrapStatus.Completed)
        {
            return CreateAdminOutcome.AlreadyCompleted;
        }

        var windowOpen = state.Status == BootstrapStatus.WindowOpen &&
                         state.WindowExpiresUtc is { } expires && nowUtc < expires;
        if (!windowOpen)
        {
            return CreateAdminOutcome.WindowClosed;
        }

        if (password.Length < options.MinPasswordLength || password.Length > options.MaxPasswordLength)
        {
            return CreateAdminOutcome.WeakPassword;
        }

        await credentialStore.SetCredentialHashAsync(PasswordHasher.Hash(password), cancellationToken);
        await stateStore.SetStateAsync(BootstrapState.CompletedState, cancellationToken);
        return CreateAdminOutcome.Succeeded;
    }
}
