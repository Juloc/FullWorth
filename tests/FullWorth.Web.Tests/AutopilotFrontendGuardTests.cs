using FullWorth.Web.Tests.Pwa;
using FullWorth.Web.Navigation;
namespace FullWorth.Web.Tests;

public sealed class AutopilotFrontendGuardTests
{
    [Fact]
    public void AutopilotMustNotAddPrimaryOrBottomNavigationItem()
    {
        // Beide Leisten entstehen seit #154 aus NavigationCatalog, nicht mehr aus einem Dokument -
        // also wird der Katalog gefragt statt abgeschriebenes Markup.
        var views = NavigationCatalog.Entries.Select(entry => entry.View).ToArray();

        // Autopilot selbst bekommt keinen Menüpunkt: eine KI-Funktion darf sich nicht in die
        // Navigation schieben, das war und bleibt die Regel.
        //
        // Insights stand hier einmal mit — als bewusst zweitrangige Fläche, nur über das
        // Dashboard-Panel erreichbar. Der Besitzer hat das umgedreht: "Für dich" ist ein normaler
        // Eintrag in der Gruppe Übersicht, weil eine Seite, die man nirgends anklicken kann, in der
        // Praxis eine Seite ist, die niemand findet. Es bleibt lesend, und der Rest dieser Klasse
        // prüft das weiter.
        foreach (var forbidden in new[] { "autopilot", "intelligence", "ai" })
            Assert.DoesNotContain(forbidden, views, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlannedAutopilotFeaturesUseSharedApiClient()
    {
        var featureRoot = Path.Combine(WwwRoot(), "features");
        var planned = new[]
        {
            "insights.js",
            "action-proposals.js",
            "scenarios.js",
            "rule-compiler.js"
        };

        foreach (var name in planned)
        {
            var path = Path.Combine(featureRoot, name);
            if (!File.Exists(path)) continue;
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("/bff/backend/", content, StringComparison.Ordinal);
            Assert.DoesNotContain("/bff/banking/", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Deploy5InsightsAreSecondaryReadOnlyFeatureSurface()
    {
        var html = WebSources.Layout();
        var feature = File.ReadAllText(Path.Combine(WwwRoot(), "pages", "insights", "page.js"));
        var appCss = File.ReadAllText(Path.Combine(WwwRoot(), "styles", "app.css"));
        var insightCssPath = Path.Combine(WwwRoot(), "pages", "insights", "page.css");

        // Die Hinweise haben zwei Orte, und das ist Absicht: ein Block auf der Startseite und eine
        // eigene Seite. Seit #154 liegt Letztere als Razor-Seite vor, der Block bleibt in der Huelle.
        var page = WebSources.Page("Insights");
        var entry = File.ReadAllText(Path.Combine(WwwRoot(), "pages", "insights", "entry.js"));

        Assert.Contains("id=\"dashboard-insights\"", WebSources.Page("Dashboard"));
        Assert.Contains("id=\"view-insights\"", page);
        Assert.Contains("id=\"insights-root\"", page);

        Assert.True(File.Exists(insightCssPath));
        Assert.Contains("/pages/insights/page.css", page);
        Assert.DoesNotContain("Autopilot Deploy 5: read-only financial insights", appCss, StringComparison.Ordinal);
        Assert.Contains("mountInsights", entry);
        // Der Block auf der Startseite wird von deren Einstieg gezeichnet, seit es app.js nicht mehr
        // gibt - dieselbe Funktion, nur an dem Ort, der die Startseite heute besitzt.
        Assert.Contains("renderDashboardInsights", File.ReadAllText(Path.Combine(
            WwwRoot(), "pages", "dashboard", "entry.js")));

        Assert.Contains("api/insights?view=", feature);
        Assert.Contains("load(ctx, 'current', 3", feature);
        Assert.Contains("data-insights-tab", feature);
        Assert.Contains("data-action=\"read\"", feature);
        Assert.Contains("data-action=\"dismiss\"", feature);
        Assert.Contains("data-action=\"snooze\"", feature);
        Assert.Contains("data-feedback=\"useful\"", feature);
        Assert.Contains("data-feedback=\"irrelevant\"", feature);
        Assert.Contains("ctx.isPrivate()", feature);
        Assert.Contains("ctx.dialog", feature);
        Assert.Contains("AbortController", feature);
        // Vertrag und Budget sollen auf der Zielseite offen stehen. Das war ein Ereignis nach dem
        // Ansichtswechsel; seit #154 ist der Wechsel eine echte Navigation, also reist die Kennung
        // in der Adresse mit - und die beiden Zielseiten lesen sie in ihrer entry.js.
        Assert.Contains("'open=' + encodeURIComponent(signal.subjectId)", feature);
        Assert.Contains(
            "emitAppEvent('contract:open'",
            File.ReadAllText(Path.Combine(WwwRoot(), "pages", "contracts", "entry.js")));
        Assert.Contains(
            "openBudgetDetail(context, open)",
            File.ReadAllText(Path.Combine(WwwRoot(), "pages", "budgets", "entry.js")));

        Assert.Contains("api/contracts/merge-preview", feature, StringComparison.Ordinal);
        Assert.Contains("api/contracts/merge-execute", feature, StringComparison.Ordinal);
        var featureWithoutAllowedContractActions = feature
            .Replace("api/contracts/merge-preview", string.Empty, StringComparison.Ordinal)
            .Replace("api/contracts/merge-execute", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("api/contracts/", featureWithoutAllowedContractActions, StringComparison.Ordinal);
        Assert.DoesNotContain("api/transactions/", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("api/budgets/", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("api/contracts/merge/", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("PriceChangeSuggestion", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("ai-badge", feature, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ai-gradient", feature, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IIntelligenceProvider", feature, StringComparison.Ordinal);

        PwaAssert.Ships("'/pages/insights/page.js'");
        PwaAssert.Ships("'/pages/insights/page.css'");
    }

    [Fact]
    public void Deploy7ContractMergeExecutionRequiresPreviewTokenAndExplicitConfirmation()
    {
        var feature = File.ReadAllText(Path.Combine(WwwRoot(), "pages", "insights", "page.js"));
        var css = File.ReadAllText(Path.Combine(WwwRoot(), "pages", "insights", "page.css"));

        Assert.Contains("contractPairIds", feature);
        Assert.Contains("api/contracts/merge-preview", feature);
        Assert.Contains("api/contracts/merge-execute", feature);
        Assert.Contains("preview.previewToken", feature);
        Assert.Contains("canonicalContractId: preview.canonicalContractId", feature);
        Assert.Contains("confirmDialog(ctx", feature);
        Assert.Contains("data-merge-execute", feature);
        Assert.Contains("data-merge-reload", feature);
        Assert.Contains("insight-merge-preview", css);
        Assert.Contains("insight-merge-stale", css);

        Assert.DoesNotContain("api/contracts/merge/", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("window.confirm", feature, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", feature, StringComparison.Ordinal);
    }

    /// <summary>
    /// Insights ist ein gewöhnlicher Menüeintrag.
    ///
    /// Hier stand einmal das Gegenteil: der Eintrag durfte in keiner der beiden Leisten vorkommen,
    /// Insights sollte nur über das Dashboard-Panel erreichbar sein. Der Besitzer hat das umgedreht,
    /// und der Grund steht im Befund zum Menüumbau — Insights war die einzige Seite, die weder am
    /// Desktop noch am Telefon einen Weg hatte.
    ///
    /// Was es nicht darf, prüft weiterhin Deploy5InsightsAreSecondaryReadOnlyFeatureSurface: genau
    /// die dort aufgezählten Endpunkte und keine anderen.
    /// </summary>
    [Fact]
    public void InsightsIsAnOrdinaryMenuEntry()
    {
        var menu = File.ReadAllText(Path.Combine(WwwRoot(), "app", "menu.js"));

        Assert.Contains("{ view: 'insights'", menu);
        // "Erreichbar" hiess bis #154: die Huelle kennt die Ansicht. Jetzt heisst es: der Katalog
        // kennt sie, und es gibt eine Razor-Seite dafuer.
        Assert.Contains(NavigationCatalog.Entries, entry => entry.View == "insights");
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker: {startMarker}");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing marker: {endMarker}");
        return text[start..(end + endMarker.Length)];
    }

    private static string WwwRoot() => Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
