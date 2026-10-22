using HostingAdmin.Core.Sessions;
using Xunit;

namespace HostingAdmin.Core.Tests;

/// <summary>
/// Session lifecycle logic tests (plan §3.5, T8/T11).
/// </summary>
public class SessionServiceTests
{
    private static SessionOptions DefaultOptions() => new()
    {
        AbsoluteLifetime = TimeSpan.FromHours(12),
        IdleTimeout = null,
    };

    [Fact]
    public void Create_ReturnsRawIdOfSufficientEntropy()
    {
        var service = new SessionService(DefaultOptions());

        var (rawId, _) = service.Create("admin", DateTime.UtcNow);

        // 32 bytes hex = 64 chars; 256 bits entropy >= 128 required (plan §3.5).
        Assert.Equal(64, rawId.Length);
    }

    [Fact]
    public void Create_GeneratesUniqueIds()
    {
        var service = new SessionService(DefaultOptions());
        var now = DateTime.UtcNow;

        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var (rawId, _) = service.Create("admin", now);
            Assert.True(seen.Add(rawId), "Session ids must be unique.");
        }
    }

    [Fact]
    public void Create_RecordStoresHashNotRawId()
    {
        var service = new SessionService(DefaultOptions());

        var (rawId, record) = service.Create("admin", DateTime.UtcNow);

        Assert.NotEqual(rawId, record.IdHash);
        Assert.Equal(SessionService.HashId(rawId), record.IdHash);
        Assert.DoesNotContain(rawId, record.IdHash);
    }

    [Fact]
    public void Create_AppliesAbsoluteLifetime()
    {
        var options = DefaultOptions();
        var service = new SessionService(options);
        var now = DateTime.UtcNow;

        var (_, record) = service.Create("admin", now);

        Assert.Equal(now + options.AbsoluteLifetime, record.ExpiresUtc);
        Assert.Null(record.IdleExpiresUtc); // idle off by default
        Assert.False(record.Revoked);
        Assert.Equal("admin", record.AdminId);
    }

    [Fact]
    public void Create_WithIdleTimeout_SetsIdleExpiration()
    {
        var options = new SessionOptions
        {
            AbsoluteLifetime = TimeSpan.FromHours(12),
            IdleTimeout = TimeSpan.FromMinutes(30),
        };
        var service = new SessionService(options);
        var now = DateTime.UtcNow;

        var (_, record) = service.Create("admin", now);

        Assert.Equal(now + options.IdleTimeout, record.IdleExpiresUtc);
    }

    [Fact]
    public void IsValid_ActiveSession_ReturnsTrue()
    {
        var service = new SessionService(DefaultOptions());
        var now = DateTime.UtcNow;
        var (_, record) = service.Create("admin", now);

        Assert.True(SessionService.IsValid(record, now.AddHours(1)));
    }

    [Fact]
    public void IsValid_PastAbsoluteExpiration_Rejected()
    {
        var service = new SessionService(DefaultOptions());
        var now = DateTime.UtcNow;
        var (_, record) = service.Create("admin", now);

        // T8: expired session is invalid.
        Assert.False(SessionService.IsValid(record, now.AddHours(12).AddSeconds(1)));
    }

    [Fact]
    public void IsValid_PastIdleExpiration_Rejected()
    {
        var options = new SessionOptions
        {
            AbsoluteLifetime = TimeSpan.FromHours(12),
            IdleTimeout = TimeSpan.FromMinutes(30),
        };
        var service = new SessionService(options);
        var now = DateTime.UtcNow;
        var (_, record) = service.Create("admin", now);

        Assert.False(SessionService.IsValid(record, now.AddMinutes(31)));
    }

    [Fact]
    public void IsValid_RevokedSession_RejectedImmediately()
    {
        var service = new SessionService(DefaultOptions());
        var now = DateTime.UtcNow;
        var (_, record) = service.Create("admin", now);
        var revoked = record with { Revoked = true };

        // T9: revoked id is rejected immediately, even well before expiration.
        Assert.False(SessionService.IsValid(revoked, now.AddSeconds(1)));
    }

    [Fact]
    public void HashId_IsDeterministicAndDistinct()
    {
        var a1 = SessionService.HashId("id-a");
        var a2 = SessionService.HashId("id-a");
        var b = SessionService.HashId("id-b");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.Equal(64, a1.Length); // SHA-256 hex
    }

    [Fact]
    public void Create_EmptyAdminId_Throws()
    {
        var service = new SessionService(DefaultOptions());

        Assert.Throws<ArgumentException>(() => service.Create("", DateTime.UtcNow));
    }
}
