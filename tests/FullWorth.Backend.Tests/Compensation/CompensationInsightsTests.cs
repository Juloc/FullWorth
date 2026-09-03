using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Backend.Tests.Compensation;

public sealed class CompensationInsightsTests
{
    [Fact]
    public void Analyze_CreatesRaiseAndPartTimeOptions()
    {
        var result = CompensationInsights.Analyze(new CompensationInsightRequest(BasicProfile(), 300m));

        Assert.Equal(3, result.SalaryRaises.Count);
        Assert.Equal(2, result.PartTime.Count);
        Assert.All(result.SalaryRaises, option => Assert.True(option.CashNetDeltaAnnual > 0m));
        Assert.All(result.PartTime, option => Assert.True(option.Calculation.EffectiveNetValuePerWorkingHour > 0m));
    }

    [Fact]
    public void Analyze_KeepsEmployerBudgetWithinRequestedAmountForGrossOption()
    {
        const decimal monthlyBudget = 300m;
        var result = CompensationInsights.Analyze(new CompensationInsightRequest(BasicProfile(), monthlyBudget));
        var gross = Assert.Single(result.EmployerBudgetOptions, x => x.Key == "budget-gross");

        Assert.InRange(gross.EmployerCostDeltaAnnual, 0m, monthlyBudget * 12m + 0.05m);
    }

    [Fact]
    public void Analyze_RanksBudgetOptionsByFullWorthValue()
    {
        var result = CompensationInsights.Analyze(new CompensationInsightRequest(BasicProfile(), 300m));

        Assert.Equal(3, result.EmployerBudgetOptions.Count);
        Assert.True(result.EmployerBudgetOptions[0].FullWorthDeltaAnnual >= result.EmployerBudgetOptions[1].FullWorthDeltaAnnual);
        Assert.True(result.EmployerBudgetOptions[1].FullWorthDeltaAnnual >= result.EmployerBudgetOptions[2].FullWorthDeltaAnnual);
    }

    private static CompensationProfileInput BasicProfile() => new(
        Name: "Current",
        AnnualGross: 60_000m,
        StateCode: "BW",
        ChildrenUnder25: 1,
        ChildlessCareSurcharge: false,
        HealthInsuranceAdditionalRatePercent: 2.9m,
        WeeklyHours: 40m,
        VacationDays: 30);
}
