using HostingAdmin.Core.Audit;

namespace HostingAdmin.Core.Auth;

/// <summary>Outcome of a login attempt (plan task 6).</summary>
public enum LoginOutcome
{
    /// <summary>Password verified; a session may be issued.</summary>
    Succeeded,

    /// <summary>Wrong password or no admin credential configured. Generic failure — no reason detail is ever exposed (D7).</summary>
    Failed,

    /// <summary>Login is locked due to too many recent failures (plan task 6: 429).</summary>
    LockedOut,
}

/// <summary>
/// Login use case for the single admin account (plan §3.3, task 6):
/// lockout check → credential verification → throttle bookkeeping.
/// Auditing is performed by the caller (it owns request context such as the
/// source IP); this service only decides the outcome and updates throttle state.
/// Failure responses are always generic — no distinction is exposed between
/// "wrong password" and "no credential configured".
/// </summary>
public sealed class LoginService(
    IAdminCredentialStore credentialStore,
    ILoginThrottleStore throttleStore,
    AuthOptions options)
{
    /// <summary>
    /// Attempts authentication. The password is never stored, logged, or
    /// included in any result (D7).
    /// </summary>
    public async Task<LoginResult> AuthenticateAsync(string password, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var throttle = await throttleStore.GetStateAsync(cancellationToken);

        // Lockout expired? Clear it and start fresh.
        if (throttle.LockedUntilUtc is { } lockedUntil && nowUtc >= lockedUntil)
        {
            throttle = LoginThrottleState.Empty;
        }
        else if (throttle.LockedUntilUtc is not null)
        {
            return LoginResult.LockedOut();
        }

        // Failure counting window elapsed? Start a fresh window.
        if (throttle.WindowStartUtc is { } windowStart && nowUtc - windowStart > options.LockoutWindow)
        {
            throttle = throttle with { FailedCount = 0, WindowStartUtc = null };
        }

        var credentialHash = await credentialStore.GetCredentialHashAsync(cancellationToken);
        if (credentialHash is null || !PasswordHasher.Verify(password, credentialHash))
        {
            await RecordFailureAsync(throttle, nowUtc);
            return LoginResult.Failed();
        }

        await throttleStore.SetStateAsync(LoginThrottleState.Empty, cancellationToken);
        return LoginResult.Successful();
    }

    private async Task RecordFailureAsync(LoginThrottleState current, DateTime nowUtc)
    {
        var windowStart = current.WindowStartUtc ?? nowUtc;
        var failedCount = current.FailedCount + 1;

        var lockedUntil = current.LockedUntilUtc;
        if (failedCount >= options.MaxFailedLoginAttempts)
        {
            lockedUntil = nowUtc + options.LockoutWindow;
        }

        await throttleStore.SetStateAsync(new LoginThrottleState
        {
            FailedCount = failedCount,
            WindowStartUtc = windowStart,
            LockedUntilUtc = lockedUntil,
        });
    }
}

/// <summary>Login outcome; carries no credential material (D7).</summary>
public sealed record LoginResult
{
    public bool Succeeded { get; private init; }

    public bool IsLockedOut { get; private init; }

    public static LoginResult Successful() => new() { Succeeded = true };

    public static LoginResult Failed() => new();

    public static LoginResult LockedOut() => new() { IsLockedOut = true };
}
