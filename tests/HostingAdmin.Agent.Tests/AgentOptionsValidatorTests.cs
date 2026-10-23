using HostingAdmin.Agent;

namespace HostingAdmin.Agent.Tests;

/// <summary>
/// Startup configuration validation (D22): the agent must fail fast on
/// invalid configuration rather than degrading at request time.
/// </summary>
public class AgentOptionsValidatorTests
{
    private static AgentOptions ValidOptions => new()
    {
        SocketPath = "/run/hostingadmin/agent.sock",
        SecretFile = "/etc/hostingadmin/agent-secret",
    };

    [Fact]
    public void Validate_accepts_valid_options()
    {
        var errors = AgentOptionsValidator.Validate(ValidOptions);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_missing_socket_path(string? socketPath)
    {
        // AgentOptions has init-only properties; construct a broken copy.
        var broken = new AgentOptions { SocketPath = socketPath!, SecretFile = ValidOptions.SecretFile };
        var errors = AgentOptionsValidator.Validate(broken);
        Assert.Contains(errors, e => e.Contains("SocketPath"));
    }

    [Theory]
    [InlineData("agent.sock")]          // relative
    [InlineData("./agent.sock")]        // relative with traversal prefix
    public void Validate_rejects_relative_socket_path(string socketPath)
    {
        var broken = new AgentOptions { SocketPath = socketPath, SecretFile = ValidOptions.SecretFile };
        var errors = AgentOptionsValidator.Validate(broken);
        Assert.Contains(errors, e => e.Contains("absolute"));
    }

    [Fact]
    public void Validate_rejects_socket_path_with_nul()
    {
        var broken = new AgentOptions { SocketPath = "/run/x\0y", SecretFile = ValidOptions.SecretFile };
        var errors = AgentOptionsValidator.Validate(broken);
        Assert.Contains(errors, e => e.Contains("NUL"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_rejects_missing_secret_file(string? secretFile)
    {
        var broken = new AgentOptions { SocketPath = ValidOptions.SocketPath, SecretFile = secretFile! };
        var errors = AgentOptionsValidator.Validate(broken);
        Assert.Contains(errors, e => e.Contains("SecretFile"));
    }

    [Fact]
    public void Validate_rejects_relative_secret_file()
    {
        var broken = new AgentOptions { SocketPath = ValidOptions.SocketPath, SecretFile = "agent-secret" };
        var errors = AgentOptionsValidator.Validate(broken);
        Assert.Contains(errors, e => e.Contains("absolute"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rejects_non_positive_concurrency(int max)
    {
        var broken = new AgentOptions
        {
            SocketPath = ValidOptions.SocketPath,
            SecretFile = ValidOptions.SecretFile,
            MaxConcurrentConnections = max,
        };
        var errors = AgentOptionsValidator.Validate(broken);
        Assert.Contains(errors, e => e.Contains("MaxConcurrentConnections"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_rejects_non_positive_timeout(int seconds)
    {
        var broken = new AgentOptions
        {
            SocketPath = ValidOptions.SocketPath,
            SecretFile = ValidOptions.SecretFile,
            RequestTimeoutSeconds = seconds,
        };
        var errors = AgentOptionsValidator.Validate(broken);
        Assert.Contains(errors, e => e.Contains("RequestTimeoutSeconds"));
    }

    [Fact]
    public void LoadSecret_rejects_short_secrets()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "short");
            Assert.ThrowsAny<InvalidOperationException>(() => AgentOptionsValidator.LoadSecret(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadSecret_trims_whitespace_and_returns_utf8_bytes()
    {
        var path = Path.GetTempFileName();
        try
        {
            var secret = new string('a', 64);
            File.WriteAllText(path, $"  {secret}\n");
            var bytes = AgentOptionsValidator.LoadSecret(path);
            Assert.Equal(64, bytes.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
