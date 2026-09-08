using FullWorth.Backend.Modules.Coach;

namespace FullWorth.Backend.Modules.Intelligence.Context;

public sealed record FinancialContextPeriod(
    DateOnly From,
    DateOnly To,
    DateOnly ComparisonFrom,
    DateOnly ComparisonTo);

public sealed record FinancialCashFlowSnapshot(
    decimal Income,
    decimal Outgoing,
    decimal Net,
    decimal PreviousIncome,
    decimal PreviousOutgoing,
    decimal PreviousNet);

public sealed record FinancialWealthSnapshot(
    decimal? NetWorth,
    decimal? LiquidAccountBalance,
    decimal? TotalDebt,
    decimal? AverageMonthlySavings,
    decimal? PreviousAverageMonthlySavings);

public sealed record FinancialCategorySnapshot(
    Guid? CategoryId,
    string Name,
    decimal Amount,
    decimal PreviousAmount,
    decimal Delta,
    decimal Share,
    decimal ReviewCoverage,
    decimal? WorthItScore,
    decimal AvoidableNegativeReviewedAmount,
    decimal BudgetOverage);

public sealed record FinancialMerchantSnapshot(
    string Name,
    decimal Amount,
    decimal PreviousAmount,
    decimal Delta,
    decimal ReviewCoverage,
    decimal? WorthItScore);

public sealed record FinancialBudgetSnapshot(
    Guid BudgetId,
    string Name,
    Guid? CategoryId,
    string Currency,
    decimal Target,
    decimal Spent,
    decimal Remaining,
    decimal PercentUsed,
    decimal ProjectedEndSpend,
    decimal ProjectedOverUnder,
    bool PartialAccess);

public sealed record FinancialAccountSnapshot(
    Guid AccountId,
    string InstitutionName,
    string DisplayName,
    string Currency,
    decimal? Balance,
    bool IncludeInNetWorth);

public sealed record FinancialContractSnapshot(
    Guid ContractId,
    string Name,
    string? ProviderName,
    string Kind,
    decimal Amount,
    string Currency,
    string BillingCycle,
    int Interval,
    DateOnly? NextDueDate,
    bool IsActive);

public sealed record FinancialTransactionSnapshot(
    Guid TransactionId,
    DateOnly Date,
    string Merchant,
    string Category,
    decimal Amount,
    string Currency);

public sealed record FinancialReviewSnapshot(
    decimal TotalOutgoingAmount,
    decimal ReviewedOutgoingAmount,
    decimal ReviewCoverage,
    decimal PositiveAmount,
    decimal NeutralAmount,
    decimal NegativeAmount,
    decimal? WorthItScore,
    int ReviewedTransactions);

public sealed record FinancialDataQualitySnapshot(
    bool IsComplete,
    int RecentTransactionCount,
    int UncategorizedRecentTransactionCount,
    string SourceVersion);

public sealed record FinancialContextSnapshot(
    Guid UserId,
    Guid FullWorthSpaceId,
    string BaseCurrency,
    DateTimeOffset AsOf,
    FinancialContextPeriod Period,
    FinancialCashFlowSnapshot CashFlow,
    FinancialWealthSnapshot Wealth,
    IReadOnlyList<FinancialCategorySnapshot> Categories,
    IReadOnlyList<FinancialMerchantSnapshot> Merchants,
    IReadOnlyList<FinancialBudgetSnapshot> Budgets,
    IReadOnlyList<FinancialAccountSnapshot> Accounts,
    IReadOnlyList<FinancialContractSnapshot> Contracts,
    IReadOnlyList<FinancialTransactionSnapshot> RecentTransactions,
    FinancialReviewSnapshot Reviews,
    FinancialDataQualitySnapshot DataQuality);

/// <summary>
/// Permission-scoped, provider-independent financial read model for Autopilot.
///
/// Deploy 1 deliberately maps the already hardened Coach read model instead of duplicating queries.
/// Later deploys can move individual domains behind this service after parity tests prove equivalent
/// results. Consumers must depend on this snapshot rather than on an AI provider.
/// </summary>
public sealed class FinancialContextSnapshotService(CoachContextBuilder coachContextBuilder)
{
    public const string SourceVersion = "coach-context-v1";

    public async Task<FinancialContextSnapshot> BuildAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var context = await coachContextBuilder.BuildForFinancialContextAsync(userId, fullWorthSpaceId, from, to, ct);
        return FromCoachContext(userId, fullWorthSpaceId, context, DateTimeOffset.UtcNow);
    }

    public static FinancialContextSnapshot FromCoachContext(
        Guid userId,
        Guid fullWorthSpaceId,
        CoachContext context,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new FinancialContextSnapshot(
            userId,
            fullWorthSpaceId,
            context.Currency,
            asOf,
            new FinancialContextPeriod(context.From, context.To, context.ComparisonFrom, context.ComparisonTo),
            new FinancialCashFlowSnapshot(
                context.Income,
                context.Outgoing,
                context.NetCashFlow,
                context.PreviousIncome,
                context.PreviousOutgoing,
                context.PreviousNetCashFlow),
            new FinancialWealthSnapshot(
                context.CurrentNetWorth,
                context.LiquidAccountBalance,
                context.TotalDebt,
                context.AverageMonthlySavings,
                context.PreviousAverageMonthlySavings),
            context.Categories.Select(x => new FinancialCategorySnapshot(
                x.CategoryId,
                x.Name,
                x.Amount,
                x.PreviousAmount,
                x.Delta,
                x.Share,
                x.ReviewCoverage,
                x.WorthItScore,
                x.AvoidableNegativeReviewedAmount,
                x.BudgetOverage)).ToList(),
            context.Merchants.Select(x => new FinancialMerchantSnapshot(
                x.Name,
                x.Amount,
                x.PreviousAmount,
                x.Delta,
                x.ReviewCoverage,
                x.WorthItScore)).ToList(),
            context.Budgets.Select(x => new FinancialBudgetSnapshot(
                x.BudgetId,
                x.Name,
                x.CategoryId,
                x.Currency,
                x.Target,
                x.Spent,
                x.Remaining,
                x.PercentUsed,
                x.ProjectedEndSpend,
                x.ProjectedOverUnder,
                x.PartialAccess)).ToList(),
            context.Accounts.Select(x => new FinancialAccountSnapshot(
                x.AccountId,
                x.InstitutionName,
                x.DisplayName,
                x.Currency,
                x.Balance,
                x.IncludeInNetWorth)).ToList(),
            context.Contracts.Select(x => new FinancialContractSnapshot(
                x.ContractId,
                x.Name,
                x.ProviderName,
                x.Kind,
                x.Amount,
                x.Currency,
                x.BillingCycle,
                x.Interval,
                x.NextDueDate,
                x.IsActive)).ToList(),
            context.RecentTransactions.Select(x => new FinancialTransactionSnapshot(
                x.TransactionId,
                x.Date,
                x.Merchant,
                x.Category,
                x.Amount,
                x.Currency)).ToList(),
            new FinancialReviewSnapshot(
                context.Reviews.TotalOutgoingAmount,
                context.Reviews.ReviewedOutgoingAmount,
                context.Reviews.ReviewCoverage,
                context.Reviews.PositiveAmount,
                context.Reviews.NeutralAmount,
                context.Reviews.NegativeAmount,
                context.Reviews.WorthItScore,
                context.Reviews.ReviewedTransactions),
            new FinancialDataQualitySnapshot(
                IsComplete: !context.Incomplete,
                RecentTransactionCount: context.RecentTransactions.Count,
                UncategorizedRecentTransactionCount: context.RecentTransactions.Count(x =>
                    string.Equals(x.Category, "Uncategorized", StringComparison.OrdinalIgnoreCase)),
                SourceVersion));
    }
}
