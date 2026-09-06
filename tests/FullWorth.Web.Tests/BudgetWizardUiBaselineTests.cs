namespace FullWorth.Web.Tests;

public sealed class BudgetWizardUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public BudgetWizardUiBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task WizardExposesFlexiblePeriodsRolloverModesAndWeeklyGroceriesPreset()
    {
        var app = await GetAsync("/app.js");
        var de = await GetAsync("/locales/de.json");

        Assert.Contains("'daily','weekly','biweekly','monthly','quarterly','yearly','paycycle','custom'", app);
        Assert.Contains("data-budget-preset=\"weekly-groceries\"", app);
        Assert.Contains("carryOver:rolloverMode!=='reset'", app);
        Assert.Contains("carryOverOverspend:rolloverMode==='full'", app);
        Assert.Contains("startDate:usesAnchor?", app);
        Assert.Contains("Rest ansparen", de);
        Assert.Contains("Rest und Überziehung übertragen", de);
        Assert.Contains("Wocheneinkauf", de);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
