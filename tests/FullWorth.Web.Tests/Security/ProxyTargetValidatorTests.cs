using FullWorth.Web.Security;

namespace FullWorth.Web.Tests.Security;

/// <summary>
/// SSRF regression (P0.1): the BFF catch-all path is attacker-controlled. Only relative paths under an
/// explicit allowlist that resolve to the exact configured origin may pass; every absolute,
/// scheme-relative, encoded, backslash- or userinfo-mangled target — and any foreign host/port — must
/// be rejected so the internal service keys can never leave for another host.
/// </summary>
public sealed class ProxyTargetValidatorTests
{
    private static readonly Uri BackendBase = new("http://fullworth-backend:8080/");
    private static readonly Uri BankingBase = new("http://fullworth-banking:8080/");
    private static readonly string[] BackendAllow = ["/api/"];
    private static readonly string[] BankingAllow = ["/api/banking/"];

    [Theory]
    // Absolute URLs to foreign hosts (the Caddy admin API is the crown-jewel target).
    [InlineData("http://caddy:2019/config/")]
    [InlineData("https://caddy:2019/config/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://fullworth-backend:9999/api/x")]      // right host, wrong port
    [InlineData("http://evil.example/api/x")]               // wrong host
    // Scheme-relative / protocol-relative.
    [InlineData("//caddy:2019/config/")]
    [InlineData("//127.0.0.1/api/x")]
    // Userinfo smuggling (parser may treat text before @ as the host).
    [InlineData("http://fullworth-backend:8080@caddy:2019/api/x")]
    // Backslash tricks (raw + encoded).
    [InlineData("api\\..\\..\\config")]
    [InlineData("api/%5c/caddy")]
    [InlineData("%2f%2fcaddy:2019/config")]
    // Double-encoding.
    [InlineData("api/%252e%252e/config")]
    // Path traversal escaping the allowlisted prefix.
    [InlineData("api/../internal/banking/connections/")]
    [InlineData("api/../../connect/enable-banking/callback")]
    // Outside the allowlist.
    [InlineData("internal/banking/connections/")]
    [InlineData("health")]
    [InlineData("")]
    public void BackendRejectsHostileTargets(string path)
    {
        var ok = ProxyTargetValidator.TryBuildTarget(BackendBase, path, string.Empty, BackendAllow, out var target);
        Assert.False(ok);
        Assert.Null(target);
    }

    [Theory]
    [InlineData("api/accounts", "/api/accounts")]
    [InlineData("/api/accounts", "/api/accounts")]
    [InlineData("api/transactions", "/api/transactions")]
    [InlineData("api/fullworth-spaces", "/api/fullworth-spaces")]
    public void BackendAcceptsAllowlistedRelativePaths(string path, string expectedAbsolutePath)
    {
        var ok = ProxyTargetValidator.TryBuildTarget(BackendBase, path, "?fullWorthSpaceId=abc", BackendAllow, out var target);
        Assert.True(ok);
        Assert.NotNull(target);
        Assert.Equal("fullworth-backend", target!.Host);
        Assert.Equal(8080, target.Port);
        Assert.Equal(expectedAbsolutePath, target.AbsolutePath);
        Assert.Equal("?fullWorthSpaceId=abc", target.Query);
    }

    [Theory]
    [InlineData("api/banking/institutions")]
    [InlineData("api/banking/connect")]
    [InlineData("api/banking/status")]
    public void BankingAcceptsOnlyBankingApiPaths(string path)
    {
        Assert.True(ProxyTargetValidator.TryBuildTarget(BankingBase, path, string.Empty, BankingAllow, out _));
    }

    [Theory]
    [InlineData("api/accounts")]                 // backend path on the banking client
    [InlineData("connect/enable-banking/callback")]
    [InlineData("http://caddy:2019/config/")]
    public void BankingRejectsNonBankingOrForeignPaths(string path)
    {
        Assert.False(ProxyTargetValidator.TryBuildTarget(BankingBase, path, string.Empty, BankingAllow, out _));
    }

    [Theory]
    [InlineData("http://fullworth-backend:8080/api/x", true)]
    [InlineData("http://fullworth-backend:8080/anything", true)]  // origin check is path-agnostic
    [InlineData("http://caddy:2019/api/x", false)]
    [InlineData("https://fullworth-backend:8080/api/x", false)]   // scheme mismatch
    [InlineData("http://fullworth-backend:9999/api/x", false)]    // port mismatch
    [InlineData("http://user@fullworth-backend:8080/api/x", false)] // userinfo
    public void IsSameOriginEnforcesSchemeHostPortAndNoUserinfo(string uri, bool expected)
    {
        Assert.Equal(expected, ProxyTargetValidator.IsSameOrigin(new Uri(uri), BackendBase));
    }

    [Fact]
    public void IsSameOriginRejectsFragments()
    {
        Assert.False(ProxyTargetValidator.IsSameOrigin(new Uri("http://fullworth-backend:8080/api/x#frag"), BackendBase));
    }
}
