using System.Text.Json;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Pwa;

/// <summary>
/// Structural guards (Wave K1) for the PWA assets. Pure file checks (no server/DB): the manifest is
/// valid and installable, and the service worker stores nothing but the page it shows without a
/// connection - so no financial data can leak into its cache via a future edit.
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

    /// <summary>
    /// Der Worker legt nichts ab ausser der Hinweisseite ohne Verbindung: kein cache.put, keine
    /// Antwort des Netzes im Cache. Damit kann keine Aenderung hier Finanzdaten in den Cache bringen,
    /// ohne diesen Test zu brechen - frueher musste er dafuer eine Liste verbotener Praefixe pflegen.
    /// </summary>
    [Fact]
    public void ServiceWorkerStoresNothingButTheOfflinePage()
    {
        var sw = File.ReadAllText(Asset("sw.js"));

        Assert.Matches(new Regex(@"const\s+VERSION\s*=", RegexOptions.None, TimeSpan.FromSeconds(1)), sw);
        Assert.DoesNotContain(".put(", sw);
        Assert.Contains("cache.addAll(OFFLINE_ASSETS)", sw);
        // Nur Seitenaufrufe, und von denen nur der gescheiterte: alles andere geht am Worker vorbei.
        Assert.Contains("if (request.mode !== 'navigate') return;", sw);
        Assert.Contains("fetch(request).catch(", sw);
    }

    [Fact]
    public void ServiceWorkerOfflineAssetsExistOnDisk()
    {
        var sw = File.ReadAllText(Asset("sw.js"));
        var shell = Between(sw, "const OFFLINE_ASSETS = [", "];");
        var entries = Regex.Matches(
                shell,
                @"['""](?<path>/[^'""]+)['""]",
                RegexOptions.None,
                TimeSpan.FromSeconds(1))
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            var relative = entry.TrimStart('/');
            Assert.True(
                File.Exists(AssetPath(relative)),
                $"service worker offline list references missing asset: {entry}");
        }
    }

    [Fact]
    public void IndexStaticAssetReferencesExistOnDisk()
    {
        var index = WebSources.Layout();
        var references = Regex.Matches(
                index,
                @"(?:src|href)=""~?(?<path>/[^""?#]+\.(?:js|mjs|css|json|svg|png|woff2?))(?:[?#][^""]*)?""",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1))
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(references);
        foreach (var reference in references)
        {
            var relative = reference.TrimStart('/');
            Assert.True(
                File.Exists(AssetPath(relative)),
                $"index.html references missing static asset: {reference}");
        }
    }

    [Fact]
    public void IndexRegistersServiceWorkerAndManifest()
    {
        var index = WebSources.Layout();
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
        var path = AssetPath(relative);
        Assert.True(File.Exists(path), $"asset not found: {path}");
        return path;
    }

    private static string AssetPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(
            directory!.FullName,
            "src",
            "FullWorth.Web",
            "wwwroot",
            relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
