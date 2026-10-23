using HostingAdmin.Agent;

namespace HostingAdmin.Agent.Tests;

/// <summary>
/// Agent-side allowlist tests (D18, TA2, TA3): exact ordinal match, strict
/// charset, injection/traversal payload rejection. The allowlist — not client
/// input — is the control.
/// </summary>
public class UnitAllowlistTests
{
    private static readonly string[] Allowlist = ["nginx.service", "php8.3-fpm.service"];

    [Theory]
    [InlineData("nginx.service")]
    [InlineData("php8.3-fpm.service")]
    public void Contains_accepts_exact_allowlist_entries(string unit)
    {
        var allowlist = new UnitAllowlist(Allowlist);
        Assert.True(allowlist.Contains(unit));
    }

    [Fact]
    public void Contains_is_case_sensitive_and_ordinal()
    {
        var allowlist = new UnitAllowlist(Allowlist);
        Assert.False(allowlist.Contains("Nginx.Service"));
        Assert.False(allowlist.Contains("nginx.Service"));
    }

    [Theory]
    [InlineData("nginx.service; reboot")]     // injection payload (TA3)
    [InlineData("nginx.service && reboot")]   // injection payload
    [InlineData("nginx.service | cat")]       // injection payload
    [InlineData("$(reboot)")]                 // command substitution
    [InlineData("`reboot`")]                  // backtick substitution
    [InlineData("../../etc/passwd")]          // traversal (TA3)
    [InlineData("nginx.service\nreboot")]     // newline smuggling
    [InlineData("nginx service")]             // whitespace
    [InlineData("nginx\t.service")]           // tab
    [InlineData("nginx.service ")]            // trailing space
    [InlineData(".hidden.service")]           // leading dot
    [InlineData("-dash.service")]             // leading dash (option-like)
    [InlineData("")]                          // empty
    [InlineData("nginx")]                     // not in allowlist
    [InlineData("nginx.servicex")]            // near-miss
    public void Contains_rejects_non_allowlisted_or_charset_violating_units(string unit)
    {
        var allowlist = new UnitAllowlist(Allowlist);
        Assert.False(allowlist.Contains(unit));
    }

    [Fact]
    public void Contains_rejects_unicode_lookalikes()
    {
        // Fullwidth characters and homoglyphs must fail the charset rule.
        var allowlist = new UnitAllowlist(Allowlist);
        Assert.False(allowlist.Contains("nginx\uFF0Eserv\uFF49ce")); // ｎｇｉｎｘ-style fullwidth
        Assert.False(allowlist.Contains("nginx\u200B.service"));     // zero-width space
    }

    [Fact]
    public void Contains_rejects_overlong_names()
    {
        var allowlist = new UnitAllowlist(Allowlist);
        Assert.False(allowlist.Contains(new string('a', 257) + ".service"));
    }

    [Fact]
    public void PassesCharset_accepts_valid_unit_names()
    {
        Assert.True(UnitAllowlist.PassesCharset("nginx.service"));
        Assert.True(UnitAllowlist.PassesCharset("php8.3-fpm.service"));
        Assert.True(UnitAllowlist.PassesCharset("user@1000.service"));
        Assert.True(UnitAllowlist.PassesCharset("my_app.service"));
    }

    [Theory]
    [InlineData("nginx.service; reboot")]
    [InlineData("../../evil")]
    [InlineData("a b")]
    [InlineData(".hidden")]
    [InlineData("-opt")]
    [InlineData("")]
    public void PassesCharset_rejects_invalid_names(string unit)
    {
        Assert.False(UnitAllowlist.PassesCharset(unit));
    }

    [Fact]
    public void Validate_rejects_empty_allowlist()
    {
        var errors = UnitAllowlist.Validate([]);
        Assert.Contains(errors, e => e.Contains("at least one"));
    }

    [Fact]
    public void Validate_rejects_null_allowlist()
    {
        var errors = UnitAllowlist.Validate(null);
        Assert.Contains(errors, e => e.Contains("at least one"));
    }

    [Fact]
    public void Validate_rejects_entries_violating_charset()
    {
        var errors = UnitAllowlist.Validate(["nginx.service", "bad unit; reboot"]);
        Assert.Single(errors);
        Assert.Contains("bad unit; reboot", errors[0]);
    }

    [Fact]
    public void Validate_accepts_valid_allowlist()
    {
        Assert.Empty(UnitAllowlist.Validate(Allowlist));
    }
}
