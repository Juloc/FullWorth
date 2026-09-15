namespace FullWorth.Web.Tests;

public sealed class BudgetWizardUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public BudgetWizardUiBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task WizardExposesFlexiblePeriodsAndRolloverModes()
    {
        var budgets = await GetAsync("/pages/budgets/page.js");
        var de = await GetAsync("/locales/de.json");

        Assert.Contains("'daily','weekly','biweekly','monthly','quarterly','yearly','paycycle','custom'", budgets);
        // budgets.js reformatted these expressions with spaces around operators when it moved out of
        // app.js; same rollover-mode/flexible-period logic, just re-pointed at the current literal text.
        Assert.Contains("carryOver: rolloverMode !== 'reset'", budgets);
        Assert.Contains("carryOverOverspend: rolloverMode === 'full'", budgets);
        Assert.Contains("startDate: usesAnchor ?", budgets);
        Assert.Contains("Rest ansparen", de);
        Assert.Contains("Rest und Überziehung übertragen", de);
    }

    /// <summary>
    /// Dieser Test stand einmal auf dem Kopf: er verlangte <c>data-budget-preset="weekly-groceries"</c>,
    /// also genau die fest verdrahtete Regel "Lebensmittel heisst woechentlich", die #115 verbietet.
    /// Ein Wächter, der das Falsche festnagelt, ist schlimmer als keiner - er macht die Korrektur zum
    /// Testbruch. Jetzt haelt er das Gegenteil fest: der Vorschlag kommt vom Server, aus den Buchungen.
    /// </summary>
    [Fact]
    public async Task NoCategoryIsWiredToAFixedPeriod()
    {
        var budgets = await GetAsync("/pages/budgets/page.js");
        var de = await GetAsync("/locales/de.json");

        Assert.DoesNotContain("data-budget-preset", budgets);
        Assert.DoesNotContain("Wocheneinkauf", de);
        Assert.Contains("api/budget-suggestions", budgets);
        // Der Vorschlag darf nichts ueberschreiben, was der Benutzer schon gesetzt hat.
        Assert.Contains("if (!amountInput.value)", budgets);
    }

    /// <summary>
    /// "Wann beginnt eine Periode?" und "ab wann rechnen wir den Uebertrag?" sind zwei Fragen (#115).
    /// Der Uebertrag ist ausserdem standardmaessig an.
    /// </summary>
    [Fact]
    public async Task CarryOverHasItsOwnStartAndIsOnByDefault()
    {
        var budgets = await GetAsync("/pages/budgets/page.js");
        var de = await GetAsync("/locales/de.json");

        Assert.Contains("carryOverStart", budgets);
        Assert.Contains("'as-far-back-as-possible','this-period','from-date'", budgets);
        Assert.Contains("Übertrag berechnen ab", de);
        // Ein neues Budget startet mit vollem Uebertrag; ein bestehendes behaelt, was es hat.
        Assert.Contains("const rollover = !existing\n    ? 'full'", budgets.Replace("\r\n", "\n"));
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
