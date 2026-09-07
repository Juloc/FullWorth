using FullWorth.Backend.Modules.Coach;
using FullWorth.Backend.Modules.Intelligence.Context;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class FinancialContextSnapshotTests
{
    [Fact]
    public void MapsCoachContextWithoutChangingFinancialValues()
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var budgetId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 7);
        var asOf = new DateTimeOffset(2026, 9, 7, 20, 30, 0, TimeSpan.Zero);

        var reviews = new SpendingReviewSummaryDto(
            from,
            to,
            "EUR",
            false,
            500m,
            300m,
            .6m,
            180m,
            20m,
            100m,
            .2667m,
            6,
            [],
            [],
            [],
            [],
            [],
            []);

        var context = new CoachContext(
            from,
            to,
            new DateOnly(2026, 8, 25),
            new DateOnly(2026, 8, 31),
            "EUR",
            false,
            3_000m,
            900m,
            2_100m,
            2_900m,
            800m,
            2_100m,
            42_000m,
            700m,
            [
                new CoachCategoryFact(categoryId, "Groceries", 220m, 180m, 40m, .2444m, .5m, .4m, 30m, 90m)
                {
                    AvoidableNegativeReviewedAmount = 12m,
                    BudgetOverage = 20m
                }
            ],
            [new CoachMerchantFact("Market", 140m, 100m, 40m, .4m, .5m, 10m, 40m)],
            reviews,
            [])
        {
            LiquidAccountBalance = 5_500m,
            TotalDebt = 12_000m,
            Budgets =
            [
                new CoachBudgetFact(budgetId, "Food", categoryId, "EUR", 400m, 300m, 100m, 75m, 430m, -30m, false)
            ],
            Accounts =
            [
                new CoachAccountFact(accountId, "Bank", "Giro", "EUR", 2_500m, true)
            ],
            Contracts =
            [
                new CoachContractFact(contractId, "Internet", "Provider", "contract", 39.99m, "EUR", "monthly", 1, new DateOnly(2026, 9, 20), true)
            ],
            RecentTransactions =
            [
                new CoachTransactionFact(transactionId, new DateOnly(2026, 9, 6), "Market", "Groceries", -45m, "EUR")
            ]
        };

        var snapshot = FinancialContextSnapshotService.FromCoachContext(userId, spaceId, context, asOf);

        Assert.Equal(userId, snapshot.UserId);
        Assert.Equal(spaceId, snapshot.FullWorthSpaceId);
        Assert.Equal("EUR", snapshot.BaseCurrency);
        Assert.Equal(asOf, snapshot.AsOf);
        Assert.Equal(from, snapshot.Period.From);
        Assert.Equal(to, snapshot.Period.To);

        Assert.Equal(context.Income, snapshot.CashFlow.Income);
        Assert.Equal(context.Outgoing, snapshot.CashFlow.Outgoing);
        Assert.Equal(context.NetCashFlow, snapshot.CashFlow.Net);
        Assert.Equal(context.PreviousIncome, snapshot.CashFlow.PreviousIncome);
        Assert.Equal(context.PreviousOutgoing, snapshot.CashFlow.PreviousOutgoing);
        Assert.Equal(context.PreviousNetCashFlow, snapshot.CashFlow.PreviousNet);

        Assert.Equal(context.CurrentNetWorth, snapshot.Wealth.NetWorth);
        Assert.Equal(context.LiquidAccountBalance, snapshot.Wealth.LiquidAccountBalance);
        Assert.Equal(context.TotalDebt, snapshot.Wealth.TotalDebt);
        Assert.Equal(context.AverageMonthlySavings, snapshot.Wealth.AverageMonthlySavings);

        var category = Assert.Single(snapshot.Categories);
        Assert.Equal(categoryId, category.CategoryId);
        Assert.Equal(220m, category.Amount);
        Assert.Equal(12m, category.AvoidableNegativeReviewedAmount);
        Assert.Equal(20m, category.BudgetOverage);

        Assert.Equal("Market", Assert.Single(snapshot.Merchants).Name);
        Assert.Equal(budgetId, Assert.Single(snapshot.Budgets).BudgetId);
        Assert.Equal(accountId, Assert.Single(snapshot.Accounts).AccountId);
        Assert.Equal(contractId, Assert.Single(snapshot.Contracts).ContractId);
        Assert.Equal(transactionId, Assert.Single(snapshot.RecentTransactions).TransactionId);

        Assert.Equal(reviews.ReviewCoverage, snapshot.Reviews.ReviewCoverage);
        Assert.Equal(reviews.WorthItScore, snapshot.Reviews.WorthItScore);
        Assert.True(snapshot.DataQuality.IsComplete);
        Assert.Equal(FinancialContextSnapshotService.SourceVersion, snapshot.DataQuality.SourceVersion);
    }

    [Fact]
    public void IncompleteCoachContextStaysIncomplete()
    {
        var from = new DateOnly(2026, 9, 1);
        var reviews = new SpendingReviewSummaryDto(
            from, from, "EUR", true,
            0m, 0m, 0m, 0m, 0m, 0m, null, 0,
            [], [], [], [], [], []);

        var context = new CoachContext(
            from, from, from.AddDays(-1), from.AddDays(-1), "EUR", true,
            0m, 0m, 0m, 0m, 0m, 0m, null, null,
            [], [], reviews, []);

        var snapshot = FinancialContextSnapshotService.FromCoachContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            context,
            DateTimeOffset.UtcNow);

        Assert.False(snapshot.DataQuality.IsComplete);
    }
}
