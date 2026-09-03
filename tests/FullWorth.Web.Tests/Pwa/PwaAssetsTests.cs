using System.Text.Json;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Pwa;

/// <summary>
/// Structural guards (Wave K1) for the PWA assets. Pure file checks (no server/DB): the manifest is
/// valid and installable, and the service worker precaches only the static shell while never caching
/// sensitive paths (finance API, BFF, auth, receipts) — so no financial data can leak into the
/// offline cache via a future edit.
/// </summary>
public sealed class PwaAssetsTests
{
    [Fact]
    public void ManifestIsValidAndInstallable()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Asset("manifest.json")));
        var root = doc.RootElement;

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());

        var icons = root.GetProperty("icons");
        Assert.True(icons.GetArrayLength() >= 1);
        foreach (var icon in icons.EnumerateArray())
            Assert.False(string.IsNullOrWhiteSpace(icon.GetProperty("src").GetString()));
    }

    [Fact]
    public void ServiceWorkerPrecachesOnlyStaticShell_NeverSensitivePaths()
    {
        var sw = File.ReadAllText(Asset("sw.js"));

        // Versioned cache so a new shell purges the old one.
        Assert.Matches(new Regex(@"const\s+VERSION\s*=", RegexOptions.None, TimeSpan.FromSeconds(1)), sw);

        // The precache list must not contain any sensitive/dynamic path. Note /share is the receipt
        // share-target ingress (manifest action "/share/receipt"); we forbid the "/share" prefix rather
        // than a bare "/receipt" substring, which would also match the legitimate static
        // /features/receipt-scan-*.js shell modules that must stay precached for offline use.
        var shell = Between(sw, "const APP_SHELL = [", "];");
        foreach (var forbidden in new[] { "/api", "/bff", "/auth", "/connect", "/share" })
            Assert.DoesNotContain(forbidden, shell);

        // The runtime guard must treat those prefixes as network-only (uncached) — mirroring isSensitive().
        foreach (var guard in new[] { "'/api'", "'/bff'", "'/auth'", "'/share'" })
            Assert.Contains(guard, sw);

        // Only GET is handled; non-GET must be passed through.
        Assert.Contains("request.method !== 'GET'", sw);
    }

    [Fact]
    public void IndexRegistersServiceWorkerAndManifest()
    {
        var index = File.ReadAllText(Asset("index.html"));
        Assert.Contains("rel=\"manifest\"", index);
        // Registration is an external script (CSP-safe), not inline.
        Assert.Contains("/pwa/register-sw.js", index);
        Assert.DoesNotContain("<script>", index); // no inline scripts (strict CSP)

        var register = File.ReadAllText(Asset("pwa/register-sw.js"));
        Assert.Contains("serviceWorker", register);
        Assert.Contains("register('/sw.js')", register);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"marker not found: {start}");
        from += start.Length;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"end marker not found: {end}");
        return text[from..to];
    }

    private static string Asset(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot", relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"asset not found: {path}");
        return path;
    }
}
