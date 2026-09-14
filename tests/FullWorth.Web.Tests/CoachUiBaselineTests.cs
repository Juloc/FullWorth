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
        // Registrierer lädt gar nichts mehr.
        Assert.DoesNotContain("import(", register);
        Assert.Contains("from './pages/coach/page.js'", await GetAsync("/app.js"));
    }

    [Fact]
    public async Task CoachShellExposesEvidenceAndDeterministicModeWithoutMandatoryAi()
    {
        var shell = await GetAsync("/pages/coach/page.js");
        var coachCss = await GetAsync("/pages/coach/page.css");
        var markup = ReadSource("index.html");
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
        Assert.Contains("id=\"coach-launcher\"", markup);
        Assert.Contains("id=\"coach-dock\"", markup);
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

        Assert.Contains("id=\"layout-reset\"", html);
        Assert.Contains("finance.sidebar.width.", app);
        Assert.Contains("fullworth:view-change", app);
        Assert.Contains("onAppEvent('budget:open'", app);
        Assert.Contains("emitAppEvent('budget:open'", coach);
        Assert.Contains("selectedForCoach", transactions);
        Assert.Contains("selectedItems", transactions);
        Assert.Contains("data-tx-select", transactions);
        Assert.Contains("fullworth:coach-open", transactions);
        Assert.Contains("fullworth:coach-open", contracts);
        Assert.Contains("fullworth:coach-open", networth);
        Assert.Contains("fullworth:coach-open", accounts);
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
