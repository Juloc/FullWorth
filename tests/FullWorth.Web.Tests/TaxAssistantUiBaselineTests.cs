using FullWorth.Web.Navigation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class TaxAssistantUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;
    private readonly FullWorthWebFactory factory;

    public TaxAssistantUiBaselineTests(FullWorthWebFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task MainApp_LoadsTaxFeatureModules()
    {
        // "/" is served by MapFallbackToFile("index.html").RequireAuthorization(), so an unauthenticated
        // client is redirected to the auth login shell. Read the shipped index.html shell directly.
        //
        // Steuern ist ein echter, verdrahteter Bereich - kein motion.js-Seiteneffekt-Import, wie es
        // die alte Flickschicht einmal war. Seit #154 ist der Bereich eine eigene Razor-Seite: das
        // Markup steht dort, der Einstieg in entry.js, und app/routes.js führt ihn als migriert.
        var html = WebSources.Page("Tax");
        var entry = ReadWebAsset(Path.Combine("pages", "tax", "entry.js"));
        var tax = await GetAsync("/pages/tax/page.js");
        var review = await GetAsync("/pages/tax/review-extra.js");

        Assert.Contains("@page \"/tax\"", html);
        Assert.Contains("id=\"view-tax\"", html);
        Assert.Contains("./page.js", entry);
        // "Erreichbar" hiess bis #154: die Huelle kennt die Ansicht. Jetzt heisst es: der Katalog
        // kennt sie, und es gibt eine Razor-Seite dafuer.
        Assert.Contains(NavigationCatalog.Entries, entry => entry.View == "tax");
        Assert.Contains("/tax/review", tax);
        Assert.Contains("api/tax/candidates", tax);
        Assert.Contains("api/tax/years/", review);
    }

    [Fact]
    public async Task TaxReview_ContainsDecisionEvidenceAndExportFlows()
    {
        var tax = await GetAsync("/pages/tax/page.js");
        var review = await GetAsync("/pages/tax/review-extra.js");

        Assert.Contains("api/tax/candidates/${id}/${action}", tax);
        Assert.Contains("'confirm'", tax);
        Assert.Contains("'reject'", tax);
        Assert.Contains("eligiblePercentage", tax);
        Assert.Contains("document-target", review);
        Assert.Contains("form.append('document'", review);
        Assert.Contains("/export?format=", review);
        Assert.Contains("data-tax-export", review);
    }

    [Fact]
    public async Task TaxSettings_ExposePersonalAndAnalysisOptOuts()
    {
        var tax = await GetAsync("/pages/tax/page.js");
        var review = await GetAsync("/pages/tax/review-extra.js");

        Assert.Contains("api/tax/profile/settings", tax);
        Assert.Contains("assistantEnabled", tax);
        Assert.Contains("analyzeTransactions", review);
        Assert.Contains("analyzePurchases", review);
        Assert.Contains("analyzeDocuments", review);
        Assert.Contains("automaticAnalysisEnabled", review);
        Assert.Contains("aiAnalysisEnabled", review);
        Assert.Contains("api/tax/data", review);
    }

    [Fact]
    public async Task TaxUi_HasResponsiveReviewAndSettingsStyles()
    {
        // Einmal gelesen. Hier standen bis #177 zwei Variablen, die beide dieselbe Datei holten -
        // ein Rest aus der Zeit, als die Pruefungs-Stile ein eigenes Blatt hatten.
        var css = await GetAsync("/pages/tax/page.css");

        Assert.Contains("tax-case", css);
        Assert.Contains("tax-year-review", css);
        Assert.Contains("tax-advanced-grid", css);
        Assert.Contains("@media", css);

        // ".tax-view" stand hier auch und ist bewusst weg: die Regel dazu war
        // ".tax-view{display:none}" plus ein ".tax-view.active{display:block}" aus der alten Huelle.
        // Mit .active blieb nur das Verstecken uebrig, und die Seite war unsichtbar. Was an ihrer
        // Stelle prueft, dass die Seite ueberhaupt etwas zeigt, ist PageContentVisibilityTests - ein
        // Selektor im Stylesheet kann das nicht, und genau deshalb hat er es damals auch nicht
        // gemerkt. Der Abschnitt im Markup traegt die Klasse weiterhin; sie ist der Anker der Seite.
        Assert.Contains("class=\"tax-view\"", WebSources.Page("Tax"), StringComparison.Ordinal);
        Assert.DoesNotContain(".tax-view.active", css);
    }

    private string ReadWebAsset(string relative)
    {
        var webRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        return File.ReadAllText(Path.Combine(webRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
