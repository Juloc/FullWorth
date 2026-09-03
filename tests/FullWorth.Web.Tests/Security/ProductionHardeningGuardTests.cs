using System.IO;

namespace FullWorth.Web.Tests.Security;

// Locks the statically-checkable P1 web-hardening invariants so they can't silently regress:
// OpenAPI stays Development-only, the auth cookie takes the __Host- prefix in Production, the public
// edge pins AllowedHosts, and reverse-proxy trust stays limited to configured known proxies.
public sealed class ProductionHardeningGuardTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Src(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

    [Fact]
    public void OpenApiIsDevelopmentOnly()
    {
        foreach (var program in new[] {
            new[] { "src", "FullWorth.Backend", "Program.cs" },
            new[] { "src", "FullWorth.Banking", "Program.cs" } })
        {
            var text = Src(program);
            // Every MapOpenApi call must be guarded by IsDevelopment.
            foreach (var line in text.Split('\n'))
                if (line.Contains("MapOpenApi(", StringComparison.Ordinal))
                    Assert.Contains("IsDevelopment", line);
        }
    }

    [Fact]
    public void AuthCookieUsesHostPrefixInProduction()
    {
        var policy = Src("src", "FullWorth.Web", "Modules", "Sessions", "SessionCookiePolicy.cs");
        Assert.Contains("__Host-", policy);
        Assert.Contains("isProduction", policy);
    }

    [Fact]
    public void PublicEdgePinsAllowedHostsInProduction()
    {
        var program = Src("src", "FullWorth.Web", "Program.cs");
        Assert.Contains("AllowedHosts", program);
        Assert.Contains("IsProduction", program);
    }

    [Fact]
    public void ReverseProxyTrustIsPinnedToKnownProxies()
    {
        var program = Src("src", "FullWorth.Web", "Program.cs");
        Assert.Contains("ReverseProxy:KnownProxies", program);
        Assert.Contains("ForwardLimit", program);
    }

    [Fact]
    public void ReceiptDownloadSetsNoSniff()
    {
        var endpoint = Src("src", "FullWorth.Backend", "Modules", "Purchases", "PurchaseCaptureEndpoints.cs");
        Assert.Contains("X-Content-Type-Options", endpoint);
        Assert.Contains("nosniff", endpoint);
    }
}
