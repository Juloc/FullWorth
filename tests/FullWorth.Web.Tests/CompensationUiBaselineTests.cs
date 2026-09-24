using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class CompensationUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;
    private readonly FullWorthWebFactory _factory;

    public CompensationUiBaselineTests(FullWorthWebFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // The calculator has always handled a partial year; for a long time nothing could enter one, so a
    // mid-year job change was silently calculated as twelve months. Three parts have to stay together:
    // the fields, the mapping in the shared profile reader, and the note that explains the smaller
    // annual figures - the mapping alone would produce correct numbers with no visible reason.
    [Fact]
    public async Task CompensationPage_CanEnterAPartialEmploymentYearAndSaysWhenItDoes()
    {
        var html = await MarkupAsync();
        var shared = await PageFileAsync("shared.js");
        var baseJs = await PageFileAsync("page.js");
        var css = await PageFileAsync("page.css");

        Assert.Contains("id=\"employment-start\"", html);
        Assert.Contains("id=\"employment-end\"", html);
        Assert.Contains("employmentStart: val('employment-start')", shared);
        Assert.Contains("employmentEnd: val('employment-end')", shared);
        Assert.Contains("setVal('employment-start'", shared);
        Assert.Contains("setVal('employment-end'", shared);
        Assert.Contains("monthsEmployedInYear", baseJs);
        Assert.Contains("comp-partial-year", baseJs);
        // Die Regel muss in dem Stylesheet stehen, das die Seite mitbringt: pages/compensation/page.css.
        Assert.Contains(".comp-partial-year", css);
    }

    [Fact]
    public async Task CompensationPage_LoadsAllFeatureModules()
    {
        var baseJs = await PageFileAsync("page.js");
        var extendedJs = await PageFileAsync("extended.js");
        var historyJs = await PageFileAsync("history.js");
        var css = await PageFileAsync("page.css");

        // Die Module hängen nicht mehr als sechs <script>-Zeilen an einem eigenen Dokument, sondern
        // werden von page.js statisch importiert. Damit ist beim ersten Besuch alles schon da, und
        // die Reihenfolge - erst die Reiter, dann die Module, die sich daran hängen - steht an einer
        // Stelle statt in einer HTML-Datei.
        Assert.Contains("import './extended.js'", baseJs);
        Assert.Contains("import './history.js'", baseJs);
        Assert.Contains("import './other-income.js'", baseJs);
        Assert.Contains("import './benchmarks.js'", baseJs);
        Assert.Contains("api/compensation/calculate", baseJs);
        Assert.Contains("api/compensation/insights", extendedJs);
        Assert.Contains("api/compensation/payslips/extract", extendedJs);
        Assert.Contains("api/compensation/history", historyJs);
        Assert.Contains("api/compensation/timeline", historyJs);
        Assert.Contains("history-chart", css);
    }

    [Fact]
    public async Task MainNavigation_ContainsCompensationEntry()
    {
        // "/" is served by MapFallbackToFile("index.html").RequireAuthorization(), so an unauthenticated
        // client is redirected to the auth login shell. Read the shipped index.html shell directly.
        var html = ReadWebAsset("index.html");
        var menu = ReadWebAsset(Path.Combine("app", "menu.js"));

        // Gehalt ist ein gewöhnlicher Eintrag aus app/menu.js und eine gewöhnliche Ansicht.
        //
        // Es hatte einmal eine handkopierte Seitenleiste in features/compensation-nav.js — dreizehn
        // Einträge als HTML-Literal, die schon veraltet waren: ohne Altersvorsorge, ohne
        // Einstellungen, ohne Coach, und "Mehr" sprang einfach auf die Startseite. Genau dafür gibt
        // es jetzt eine Menüquelle, und diese Datei ist gelöscht.
        Assert.Contains("{ view: 'compensation'", menu);
        Assert.DoesNotContain("href: '/compensation.html'", menu);
        Assert.Contains("data-entry=\"compensation\"", html);
        // Das Markup der Seite liegt seit #154 nicht mehr in der Hülle, sondern bei der Seite.
        Assert.Contains("id=\"view-compensation\"", await MarkupAsync());
        Assert.False(File.Exists(Path.Combine(
            _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath,
            "features", "compensation-nav.js")));
    }

    // Eine Datei der Seite, aus der Quelle gelesen. Über die Adresse geholt käme immer die ganze
    // Hülle zurück, und darin stünde jede Zusicherung auch dann, wenn sie diese Seite nichts angeht.
    private async Task<string> PageFileAsync(string name)
    {
        var root = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        return await File.ReadAllTextAsync(Path.Combine(root, "pages", "compensation", name));
    }

    // Das Markup der Seite. Es lag bis #154 als page.html neben page.js und steht seitdem in der
    // Razor-Seite - dieselbe Datei, nur an dem Ort, an dem der Server sie ausliefert.
    private async Task<string> MarkupAsync()
    {
        var root = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        return await File.ReadAllTextAsync(Path.Combine(
            Path.GetDirectoryName(root)!, "Pages", "Compensation", "Index.cshtml"));
    }

    private string ReadWebAsset(string relative)
    {
        var webRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        return File.ReadAllText(Path.Combine(webRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
