using HostingAdmin.Core.Auth;
using Xunit;

namespace HostingAdmin.Core.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_ProducesPbkdf2Format_WithCurrentIterations()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.StartsWith($"pbkdf2-sha256${PasswordHasher.Iterations}$", hash);
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = PasswordHasher.Hash("s3cret-password");

        Assert.True(PasswordHasher.Verify("s3cret-password", hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("s3cret-password");

        Assert.False(PasswordHasher.Verify("wrong-password", hash));
    }

    [Fact]
    public void Verify_MalformedStoredHash_ReturnsFalse()
    {
        Assert.False(PasswordHasher.Verify("anything", "not-a-hash"));
        Assert.False(PasswordHasher.Verify("anything", "pbkdf2-sha256$abc$notbase64$notbase64"));
        Assert.False(PasswordHasher.Verify("anything", "argon2id$5$abc$def"));
        Assert.False(PasswordHasher.Verify("anything", ""));
    }

    [Fact]
    public void Hash_UniqueSalts_ProduceDifferentHashesForSamePassword()
    {
        var first = PasswordHasher.Hash("identical");
        var second = PasswordHasher.Hash("identical");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("identical", first));
        Assert.True(PasswordHasher.Verify("identical", second));
    }
}
