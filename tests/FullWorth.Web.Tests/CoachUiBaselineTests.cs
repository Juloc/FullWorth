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
        Assert.Contains("Deterministisch", shell);
        Assert.Contains("Verwendete Fakten", shell);
        Assert.Contains("Finanzanalyse ohne KI-Zwang", shell);
        Assert.Contains("Wo ist mein Geld hin?", shell);
        Assert.Contains("Was habe ich bereut?", shell);
        Assert.Contains("Was war es wert?", shell);
        Assert.Contains("Wann erreiche ich 100.000 €?", shell);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
