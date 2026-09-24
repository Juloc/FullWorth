namespace FullWorth.Web.Tests;

public sealed class CoachUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public CoachUiBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task CoachExtensionsUseOnlyAuthenticatedBffForFinanceData()
    {
        // transaction-review-controls.js was merged into the Coach module by the architecture
        // cleanup; its spending-review sentiment controls now live inline in the page below.
        var shell = await GetAsync("/pages/coach/page.js");
        var apiClient = await GetAsync("/core/api.js");
        var register = await GetAsync("/pwa/register-sw.js");

        // Coach no longer builds /bff/* URLs itself: it delegates every finance-data call to the
        // shared authenticated BFF client (core/services.js -> core/api.js), which is the only place
        // allowed to construct /bff/backend and /bff/banking URLs (also enforced by
        // FrontendArchitectureGuardTests.NoNewFeatureMayCallBffDirectly).
        Assert.Contains("import { api as sharedApi } from '../../core/services.js'", shell);
        Assert.Contains("api/fullworth-spaces", shell);
        Assert.DoesNotContain("/bff/backend", shell);
        Assert.DoesNotContain("/bff/banking", shell);
        Assert.DoesNotContain("fetch('/api", shell);
        Assert.DoesNotContain("fetch(`/api", shell);
        Assert.Contains("service !== 'backend' && service !== 'banking'", apiClient);
        Assert.Contains("return `/bff/${service}/${withSpace(path)}`;", apiClient);

        Assert.Contains("api/spending-reviews/transactions/${tx.id}", shell);
        Assert.Contains("sentimentButton('Positive'", shell);
        Assert.Contains("sentimentButton('Neutral'", shell);
        Assert.Contains("sentimentButton('Negative'", shell);
        Assert.Contains("data-sentiment=\"${sentiment}\"", shell);

        // Coach wurde einmal nachgeladen: register-sw.js holte es per import() nach dem Zeichnen.
        // Jetzt ist es eine Seite wie jede andere, also lädt der Einstieg es fest mit - und der
        // Registrierer lädt gar nichts mehr. Seit #154 ist dieser Einstieg die Seite selbst.
        Assert.DoesNotContain("import(", register);
        Assert.Contains("from './page.js'", await GetAsync("/pages/coach/entry.js"));
    }

    [Fact]
    public async Task CoachShellExposesEvidenceAndDeterministicModeWithoutMandatoryAi()
    {
        var shell = await GetAsync("/pages/coach/page.js");
        var coachCss = await GetAsync("/pages/coach/page.css");
        // Seit #154 liegt das Markup der Seite bei der Seite, nicht mehr in dem einen Dokument.
        var markup = WebSources.Page("Coach");
        // Die Beschriftungen stehen jetzt im Markup und in den Sprachdateien, nicht mehr zweisprachig
        // im Modul.
        Assert.Contains("coach.mode", markup);
        Assert.Contains("Lokale Auswertung", ReadSource(Path.Combine("locales", "de.json")));
        Assert.Contains("Local analysis", ReadSource(Path.Combine("locales", "en.json")));
        Assert.Contains("Verwendete Fakten", shell);
        Assert.Contains("FullWorth-Daten im sicheren Kontext", shell);
        Assert.Contains("id=\"coach-model\"", markup);
        Assert.Contains("finance.coach.model", shell);
        Assert.Contains("api/coach/models", shell);
        Assert.Contains("model: selectedModel || null", shell);
        Assert.Contains("Was ist diesen Monat wichtig?", shell);
        Assert.Contains("Was hat sich gegenüber dem letzten Zeitraum verändert?", shell);
        Assert.Contains("Wo könnte ich sinnvoll reduzieren?", shell);
        Assert.Contains("Wann erreiche ich 100.000 €?", shell);
        Assert.Contains("Wobei soll ich helfen?", shell);
        Assert.Contains("coach-suggestion-list", shell);
        Assert.Contains("coach-followups", shell);
        // Sprechblase und Dock stehen im Dokument, nicht im Modul: sie begleiten jede Seite, und
        // was vor dem ersten Zeichnen da ist, kann nichts mehr verschieben.
        // Der schwebende Starter gehoert der Huelle und ist auf jeder Seite da; das Bedienfeld
        // gehoert der Coach-Seite. Zwei Orte, und das ist richtig so.
        Assert.Contains("id=\"coach-launcher\"", ReadSource("index.html"));
        // Auch der Andockbereich gehoert der Huelle - er schwebt ueber jeder Seite.
        Assert.Contains("id=\"coach-dock\"", ReadSource("index.html"));
        Assert.Contains("finance.coach.quickAccess", shell);
        Assert.Contains("restartConversation", shell);
        Assert.Contains("api/coach/conversations?limit=1", shell);
        Assert.Contains("finance.coach.pageContext", shell);
        Assert.Contains("capturePageContext", shell);
        Assert.Contains("uiContext", shell);
        Assert.Contains("Kontext", shell);
        Assert.Contains("finance.coach.pinned", shell);
        Assert.Contains("fullworth:coach-open", shell);
        Assert.Contains("coach-context-chip", shell);
        Assert.Contains("starterSuggestions", shell);
        Assert.Contains("renderContextActions", shell);
        Assert.Contains("dockWidthMode", shell);
        Assert.Contains("initDockSwipe", shell);
        Assert.Contains("if (!question || responding) return;", shell);
        Assert.Contains("setThinking(true)", shell);
        Assert.Contains("coach-thinking-dots", shell);
        Assert.Contains("installComposerKeyboard", shell);
        Assert.Contains("input.closest('form')?.requestSubmit()", shell);
        Assert.Contains("Number(numbered[2]) + 1", shell);
        Assert.Contains("currentLine.slice(marker[0].length).trim() === ''", shell);
        Assert.Contains("event.shiftKey", shell);
        Assert.Contains("resizeComposer", shell);
        Assert.Contains("finance.coach.draft", shell);
        Assert.Contains("AbortController", shell);
        Assert.Contains("stopCoachResponse", shell);
        Assert.Contains("Antwort stoppen", shell);
        Assert.Contains("Analysiert den ausgewählten Kontext", shell);
        Assert.Contains("navigator.clipboard.writeText", shell);
        Assert.Contains("Neu generieren", shell);
        Assert.Contains("Bearbeiten und erneut senden", shell);
        Assert.Contains("Erneut versuchen", shell);
        Assert.Contains("isNearBottom", shell);
        Assert.Contains("@keyframes coach-thinking-bounce", coachCss);
        Assert.Contains("prefers-reduced-motion:reduce", coachCss);
    }

    [Fact]
    public async Task CoachUxIntegratesWithFinanceObjectsAndResponsiveLayout()
    {
        var app = await GetAsync("/app.js");
        var html = ReadSource("index.html");
        var transactions = await GetAsync("/pages/transactions/page.js");
        var contracts = await GetAsync("/pages/contracts/page.js");
        var networth = await GetAsync("/pages/networth/page.js");
        var accounts = await GetAsync("/pages/accounts/presentation.js");
        var coach = await GetAsync("/pages/coach/page.js");
        var dialogs = await GetAsync("/components/dialog.js");

        // "Layout zuruecksetzen" steht auf der Einstellungsseite - die Huelle liest ihn nur.
        Assert.Contains("id=\"layout-reset\"", WebSources.Page("Settings"));
        // Der pro Breite getrennte Schluessel steht seit #154 in app/shell.js: Coach und
        // Seitenleiste teilen sich den Platz, und beide muessen dieselbe Zahl lesen.
        Assert.Contains("finance.sidebar.width.", await GetAsync("/app/shell.js"));
        Assert.Contains("fullworth:view-change", app);
        // "Budget oeffnen" fuehrt auf eine andere Seite. Solange beide dasselbe Dokument waren,
        // ging das als Ereignis nach dem Wechsel; seit #154 ist der Wechsel eine echte Navigation
        // und das Ereignis kaeme nirgends an - die Kennung reist deshalb in der Adresse mit, und
        // der Einstieg der Budgetseite liest sie dort.
        Assert.Contains("navigate('budgets',{query:'open='", coach);
        Assert.Contains("openBudgetDetail(context, open)", await GetAsync("/pages/budgets/entry.js"));
        // Der Auswahlzustand lebt seit der gemeinsamen Auswahl-Komponente in einer
        // components/selection-list.js-Instanz statt in einer seiteneigenen Map (siehe #160).
        Assert.Contains("coachSelection", transactions);
        Assert.Contains("selectedItems", transactions);
        Assert.Contains("data-tx-select", transactions);
        Assert.Contains("fullworth:coach-open", transactions);
        Assert.Contains("fullworth:coach-open", contracts);
        Assert.Contains("fullworth:coach-open", networth);
        // Der Coach haengt nicht mehr als dauerhafter Knopf in jeder Kontozeile (#125): er steht im
        // Menue hinter dem Auslassungszeichen, also in der Seite statt in ihrer Dekoration.
        Assert.Contains("fullworth:coach-open", await GetAsync("/pages/accounts/page.js"));
        Assert.Contains("installMobileSwipe", dialogs);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot", relativePath));
    }
}
