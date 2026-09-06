namespace FullWorth.Web.Tests;

public sealed class CoachUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public CoachUiBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task CoachExtensionsUseOnlyAuthenticatedBffForFinanceData()
    {
        var shell = await GetAsync("/features/coach-shell.js");
        var reviews = await GetAsync("/features/transaction-review-controls.js");
        var register = await GetAsync("/pwa/register-sw.js");

        Assert.Contains("/bff/backend/api/fullworth-spaces", shell);
        Assert.Contains("/bff/backend/${base}", shell);
        Assert.DoesNotContain("fetch('/api", shell);
        Assert.DoesNotContain("fetch(`/api", shell);

        Assert.Contains("/bff/backend/${base}", reviews);
        Assert.Contains("api/spending-reviews/transactions/${lastTransactionId}", reviews);
        Assert.Contains("data-sentiment=\"Positive\"", reviews);
        Assert.Contains("data-sentiment=\"Neutral\"", reviews);
        Assert.Contains("data-sentiment=\"Negative\"", reviews);
        Assert.DoesNotContain("fetch('/api", reviews);
        Assert.DoesNotContain("fetch(`/api", reviews);

        Assert.Contains("import('/features/coach-shell.js')", register);
        Assert.Contains("import('/features/transaction-review-controls.js')", register);
    }

    [Fact]
    public async Task CoachShellExposesEvidenceAndDeterministicModeWithoutMandatoryAi()
    {
        var shell = await GetAsync("/features/coach-shell.js");
        var coachCss = await GetAsync("/features/coach.css");
        Assert.Contains("Deterministisch", shell);
        Assert.Contains("Verwendete Fakten", shell);
        Assert.Contains("FullWorth-Daten im sicheren Kontext", shell);
        Assert.Contains("id=\"coach-model\"", shell);
        Assert.Contains("finance.coach.model", shell);
        Assert.Contains("api/coach/models", shell);
        Assert.Contains("model: selectedModel || null", shell);
        Assert.Contains("Wo ist mein Geld hin?", shell);
        Assert.Contains("Was habe ich bereut?", shell);
        Assert.Contains("Was war es wert?", shell);
        Assert.Contains("Wann erreiche ich 100.000 €?", shell);
        Assert.Contains("launcher.id = 'coach-launcher'", shell);
        Assert.Contains("dock.id = 'coach-dock'", shell);
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
        Assert.Contains("starterQuestions", shell);
        Assert.Contains("renderContextActions", shell);
        Assert.Contains("dockWidthMode", shell);
        Assert.Contains("initDockSwipe", shell);
        Assert.Contains("if (!question || responding) return;", shell);
        Assert.Contains("setThinking(true)", shell);
        Assert.Contains("coach-thinking-dots", shell);
        Assert.Contains("@keyframes coach-thinking-bounce", coachCss);
        Assert.Contains("prefers-reduced-motion:reduce", coachCss);
    }

    [Fact]
    public async Task CoachUxIntegratesWithFinanceObjectsAndResponsiveLayout()
    {
        var app = await GetAsync("/app.js");
        var html = ReadSource("index.html");
        var transactions = await GetAsync("/features/transactions.js");
        var contracts = await GetAsync("/features/contracts.js");
        var networth = await GetAsync("/features/networth.js");
        var accounts = await GetAsync("/features/accounts-ux.js");
        var dialogs = await GetAsync("/ui/dialog.js");

        Assert.Contains("id=\"layout-reset\"", html);
        Assert.Contains("finance.sidebar.width.", app);
        Assert.Contains("fullworth:view-change", app);
        Assert.Contains("window.fwOpenBudget", app);
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
