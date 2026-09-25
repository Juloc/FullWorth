using System.Net;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Pwa;

/// <summary>
/// Die Hinweisseite ohne Verbindung (offline/index.html) - das Einzige, was der Service Worker cacht.
///
/// Bis #154 hielt der Worker einen Vorrat von ueber hundert Dateien fuer einen Kaltstart ohne Netz.
/// Der konnte nie gelingen: der Worker cacht das HTML der Anwendung nicht (es traegt persoenliche
/// Daten), und ohne ihr HTML oeffnet keine Seite. Die Tests, die hier standen, prueften den Vorrat
/// gewissenhaft in drei Richtungen - und keiner fragte, ob eine Seite damit je offline aufging. Auch
/// Gehalt, das als die offline nutzbare Ausnahme galt, rechnet auf dem Server.
///
/// Was stattdessen gilt und hier festgehalten wird: ohne Netz erscheint diese Seite statt der
/// Fehlerseite des Browsers, sie braucht dafuer nichts ausser dem, was der Worker vorhaelt, und sie
/// kommt ohne Anmeldung - sonst scheitert schon die Installation des Workers.
/// </summary>
public sealed class PwaOfflinePageTests(FullWorthWebFactory factory) : IClassFixture<FullWorthWebFactory>
{
    private const string OfflinePage = "/offline/index.html";

    [Fact]
    public void The_worker_keeps_exactly_what_the_offline_page_loads()
    {
        var loaded = LoadedByTheOfflinePage();
        // Findet das Muster nichts, waere der Vergleich unten ueber zwei fast leere Listen gruen.
        Assert.Contains("/offline/offline.js", loaded);
        Assert.Contains("/fonts/nunito-variable.woff2", loaded);

        Assert.Equal(loaded.Order(StringComparer.Ordinal), OfflineAssets().Order(StringComparer.Ordinal));
    }

    /// <summary>Ohne Netz gibt es keinen Server - die Seite darf keinen fragen.</summary>
    [Fact]
    public void The_offline_page_asks_no_server()
    {
        var page = WebSources.Asset("offline", "index.html");
        foreach (var prefix in new[] { "/api", "/bff", "/auth" })
            Assert.DoesNotContain($"\"{prefix}", page, StringComparison.Ordinal);

        var script = WebSources.Asset("offline", "offline.js");
        Assert.DoesNotContain("fetch(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("import", script, StringComparison.Ordinal);
    }

    /// <summary>Jeder Text steht in beiden Sprachen; offline.js tauscht ihn nach derselben Regel wie core/state.js.</summary>
    [Fact]
    public void Every_text_has_its_english_counterpart()
    {
        var texts = Regex.Matches(WebSources.Asset("offline", "index.html"), """data-en="(?<en>[^"]*)"[^>]*>(?<de>[^<]*)<""")
            .ToArray();

        Assert.Equal(3, texts.Length);
        Assert.All(texts, text =>
        {
            Assert.False(string.IsNullOrWhiteSpace(text.Groups["en"].Value));
            Assert.False(string.IsNullOrWhiteSpace(text.Groups["de"].Value));
        });
    }

    /// <summary>
    /// cache.addAll scheitert an einer einzigen Antwort, die nicht 200 ist - und eine Umleitung zur
    /// Anmeldung ist keine. Dann installiert sich der Worker gar nicht, und das faellt niemandem auf,
    /// weil online alles geht.
    /// </summary>
    [Fact]
    public async Task Every_file_it_keeps_comes_without_signing_in()
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var path in OfflineAssets())
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode} to an anonymous request");
        }
    }

    /// <summary>Was die Seite laedt: Stilblaetter, Skripte, Bilder, dazu die Schriften ihrer Stilblaetter.</summary>
    private static HashSet<string> LoadedByTheOfflinePage()
    {
        var loaded = Regex.Matches(WebSources.Asset("offline", "index.html"), """(?:src|href)="(?<path>/[^"?#]+)""")
            .Select(match => match.Groups["path"].Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var sheet in loaded.Where(path => path.EndsWith(".css", StringComparison.Ordinal)).ToArray())
            foreach (Match font in Regex.Matches(WebSources.Asset(sheet.TrimStart('/').Split('/')), """url\("(?<path>/[^"]+)"\)"""))
                loaded.Add(font.Groups["path"].Value);
        loaded.Add(OfflinePage);
        return loaded;
    }

    private static string[] OfflineAssets()
    {
        var sw = WebSources.Asset("sw.js");
        Assert.Contains($"const OFFLINE_PAGE = '{OfflinePage}';", sw, StringComparison.Ordinal);
        var list = sw[(sw.IndexOf("const OFFLINE_ASSETS = [", StringComparison.Ordinal) + 1)..];
        list = list[..list.IndexOf("];", StringComparison.Ordinal)];
        return Regex.Matches(list, """'(?<path>/[^']+)'""")
            .Select(match => match.Groups["path"].Value)
            .Append(OfflinePage)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
