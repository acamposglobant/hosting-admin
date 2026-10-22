using HostingAdmin.Core.Auth;
using Xunit;

namespace HostingAdmin.Core.Tests;

public class LoginServiceTests
{
    private const string CorrectPassword = "correct-password";
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeCredentialStore(string? hash) : IAdminCredentialStore
    {
        public Task<string?> GetCredentialHashAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(hash);

        public Task SetCredentialHashAsync(string credentialHash, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeThrottleStore(LoginThrottleState? initial = null) : ILoginThrottleStore
    {
        public LoginThrottleState State { get; set; } = initial ?? LoginThrottleState.Empty;

        public int WriteCount { get; private set; }

        public Task<LoginThrottleState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(State);

        public Task SetStateAsync(LoginThrottleState state, CancellationToken cancellationToken = default)
        {
            State = state;
            WriteCount++;
            return Task.CompletedTask;
        }
    }

    private static (LoginService Service, FakeThrottleStore Throttle) CreateService(
        string? credentialHash = null,
        int maxFailed = 3,
        int lockoutMinutes = 15)
    {
        var options = new AuthOptions
        {
            MaxFailedLoginAttempts = maxFailed,
            LockoutWindow = TimeSpan.FromMinutes(lockoutMinutes),
        };
        var throttle = new FakeThrottleStore();
        var service = new LoginService(
            new FakeCredentialStore(credentialHash ?? PasswordHasher.Hash(CorrectPassword)),
            throttle,
            options);
        return (service, throttle);
    }

    [Fact]
    public async Task Authenticate_CorrectPassword_Succeeds_AndResetsThrottle()
    {
        var (service, throttle) = CreateService();
        throttle.State = new LoginThrottleState { FailedCount = 2, WindowStartUtc = Now.AddMinutes(-1) };

        var result = await service.AuthenticateAsync(CorrectPassword, Now);

        Assert.True(result.Succeeded);
        Assert.Equal(0, throttle.State.FailedCount);
        Assert.Null(throttle.State.LockedUntilUtc);
    }

    [Fact]
    public async Task Authenticate_WrongPassword_Fails_AndIncrementsCounter()
    {
        var (service, throttle) = CreateService();

        var result = await service.AuthenticateAsync("wrong", Now);

        Assert.False(result.Succeeded);
        Assert.False(result.IsLockedOut);
        Assert.Equal(1, throttle.State.FailedCount);
        Assert.Equal(Now, throttle.State.WindowStartUtc);
    }

    [Fact]
    public async Task Authenticate_NoCredentialConfigured_FailsGenerically()
    {
        var options = new AuthOptions { MaxFailedLoginAttempts = 3 };
        var throttle = new FakeThrottleStore();
        var service = new LoginService(new FakeCredentialStore(null), throttle, options);

        var result = await service.AuthenticateAsync(CorrectPassword, Now);

        Assert.False(result.Succeeded);
        Assert.False(result.IsLockedOut);
        Assert.Equal(1, throttle.State.FailedCount);
    }

    [Fact]
    public async Task Authenticate_LocksAfterMaxFailures()
    {
        var (service, throttle) = CreateService(maxFailed: 3);

        await service.AuthenticateAsync("wrong", Now);
        await service.AuthenticateAsync("wrong", Now.AddSeconds(1));
        var third = await service.AuthenticateAsync("wrong", Now.AddSeconds(2));

        Assert.False(third.Succeeded);
        Assert.Equal(3, throttle.State.FailedCount);
        Assert.Equal(Now.AddSeconds(2).AddMinutes(15), throttle.State.LockedUntilUtc);

        // The next attempt is locked out even with the correct password.
        var fourth = await service.AuthenticateAsync(CorrectPassword, Now.AddSeconds(3));
        Assert.True(fourth.IsLockedOut);
        Assert.False(fourth.Succeeded);
    }

    [Fact]
    public async Task Authenticate_LockoutExpires_AfterWindow()
    {
        var (service, throttle) = CreateService(maxFailed: 1);
        await service.AuthenticateAsync("wrong", Now);
        Assert.NotNull(throttle.State.LockedUntilUtc);

        // Still locked inside the window.
        var during = await service.AuthenticateAsync(CorrectPassword, Now.AddMinutes(14));
        Assert.True(during.IsLockedOut);

        // Lockout cleared after the window; correct password succeeds.
        var after = await service.AuthenticateAsync(CorrectPassword, Now.AddMinutes(15).AddSeconds(1));
        Assert.True(after.Succeeded);
        Assert.Equal(0, throttle.State.FailedCount);
    }

    [Fact]
    public async Task Authenticate_FailureWindowElapsed_ResetsCounter()
    {
        var (service, throttle) = CreateService(maxFailed: 3, lockoutMinutes: 15);

        await service.AuthenticateAsync("wrong", Now);
        await service.AuthenticateAsync("wrong", Now.AddMinutes(10));

        // New window starts after the 15-minute window from the first failure.
        var later = await service.AuthenticateAsync("wrong", Now.AddMinutes(16));

        Assert.False(later.IsLockedOut);
        Assert.Equal(1, throttle.State.FailedCount);
        Assert.Equal(Now.AddMinutes(16), throttle.State.WindowStartUtc);
    }

    [Fact]
    public async Task Authenticate_SuccessAfterFailures_ResetsCounter()
    {
        var (service, throttle) = CreateService(maxFailed: 3);

        await service.AuthenticateAsync("wrong", Now);
        await service.AuthenticateAsync("wrong", Now.AddSeconds(1));
        var success = await service.AuthenticateAsync(CorrectPassword, Now.AddSeconds(2));
        Assert.True(success.Succeeded);

        // Failures after a successful login start from zero again.
        await service.AuthenticateAsync("wrong", Now.AddSeconds(3));
        Assert.Equal(1, throttle.State.FailedCount);
        Assert.Null(throttle.State.LockedUntilUtc);
    }
}
