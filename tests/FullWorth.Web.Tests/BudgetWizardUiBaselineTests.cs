namespace FullWorth.Web.Tests;

public sealed class BudgetWizardUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public BudgetWizardUiBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task WizardExposesFlexiblePeriodsRolloverModesAndWeeklyGroceriesPreset()
    {
        var budgets = await GetAsync("/features/budgets.js");
        var de = await GetAsync("/locales/de.json");

        Assert.Contains("'daily','weekly','biweekly','monthly','quarterly','yearly','paycycle','custom'", budgets);
        Assert.Contains("data-budget-preset=\"weekly-groceries\"", budgets);
        // budgets.js reformatted these expressions with spaces around operators when it moved out of
        // app.js; same rollover-mode/flexible-period logic, just re-pointed at the current literal text.
        Assert.Contains("carryOver: rolloverMode !== 'reset'", budgets);
        Assert.Contains("carryOverOverspend: rolloverMode === 'full'", budgets);
        Assert.Contains("startDate: usesAnchor ?", budgets);
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
