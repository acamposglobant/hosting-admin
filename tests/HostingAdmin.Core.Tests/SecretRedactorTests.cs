using HostingAdmin.Core.Logging;
using Xunit;

namespace HostingAdmin.Core.Tests;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("ADMIN_PASSWORD")]
    [InlineData("proxy_secret")]
    [InlineData("ProxySecret")]
    [InlineData("session_id")]
    [InlineData("SessionId")]
    [InlineData("apiKey")]
    [InlineData("Authorization")]
    [InlineData("cookie")]
    [InlineData("adminPasswordHash")]
    public void IsDenylistedKey_DetectsDenylistFragments_CaseInsensitive(string key)
    {
        Assert.True(SecretRedactor.IsDenylistedKey(key));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("status")]
    [InlineData("cpuPercent")]
    [InlineData("uptime")]
    [InlineData("")]
    public void IsDenylistedKey_AllowsSafeKeys(string key)
    {
        Assert.False(SecretRedactor.IsDenylistedKey(key));
    }

    [Fact]
    public void RedactValue_DenylistedKey_ReturnsPlaceholder()
    {
        Assert.Equal(SecretRedactor.RedactedPlaceholder, SecretRedactor.RedactValue("password", "hunter2"));
    }

    [Fact]
    public void RedactValue_SafeKey_ReturnsOriginalValue()
    {
        Assert.Equal("active", SecretRedactor.RedactValue("status", "active"));
    }

    [Theory]
    [InlineData("""{"password":"hunter2","status":"ok"}""", """{"password": "***REDACTED***","status":"ok"}""")]
    [InlineData("""{"Password":"hunter2"}""", """{"Password": "***REDACTED***"}""")]
    [InlineData("""{"proxy_secret":"abc123","name":"nginx"}""", """{"proxy_secret": "***REDACTED***","name":"nginx"}""")]
    [InlineData("""{"session_id":"s-123","count":3}""", """{"session_id": "***REDACTED***","count":3}""")]
    public void RedactJsonString_ScrubsDenylistedValues_KeepsSafeValues(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactJsonString(input));
    }

    [Fact]
    public void RedactJsonString_SafeJson_IsUnchanged()
    {
        const string input = """{"status":"healthy","cpuPercent":12.5}""";
        Assert.Equal(input, SecretRedactor.RedactJsonString(input));
    }

    [Fact]
    public void RedactJsonString_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SecretRedactor.RedactJsonString(string.Empty));
    }

    // --- Regression tests (security review F2/F3): non-JSON secret formats ---

    [Theory]
    [InlineData("Server=db;Password=hunter2;Database=app", "Server=db;Password=***REDACTED***;Database=app")]
    [InlineData("password=hunter2", "password=***REDACTED***")]
    [InlineData("https://x/api?token=abc123&take=5", "https://x/api?token=***REDACTED***&take=5")]
    [InlineData("session_id=s-123 user=alice", "session_id=***REDACTED*** user=alice")]
    public void RedactJsonString_KeyValuePairs_AreRedacted(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactJsonString(input));
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOi.eyJzdWIi", "Authorization: Bearer ***REDACTED***")]
    [InlineData("Bearer abc123", "Bearer ***REDACTED***")]
    [InlineData("Basic dXNlcjpwYXNz", "Basic ***REDACTED***")]
    public void RedactJsonString_AuthCredentials_AreRedacted(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactJsonString(input));
    }

    [Fact]
    public void RedactJsonString_SafeKeyValue_IsUnchanged()
    {
        const string input = "status=active count=3";
        Assert.Equal(input, SecretRedactor.RedactJsonString(input));
    }

    [Theory]
    [InlineData("encryption_key")]
    [InlineData("PrivateKey")]
    [InlineData("client_secret_value")]
    public void IsDenylistedKey_CoversKeyAndPrivateFragments(string key)
    {
        Assert.True(SecretRedactor.IsDenylistedKey(key));
    }
}
