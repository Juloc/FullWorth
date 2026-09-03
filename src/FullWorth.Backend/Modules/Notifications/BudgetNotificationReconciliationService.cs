using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Budgets.Cycles;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Parity;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Notifications;

public sealed record ReconciledBudgetNotificationSignal(
    Guid BudgetId,
    string Name,
    decimal PercentUsed,
    DateOnly PeriodStart,
    decimal NearThreshold,
    decimal CriticalThreshold,
    bool PartialAccess,
    bool IncompleteFx);

/// <summary>
/// Computes a budget alert for one recipient using exactly the same split/refund/transfer/FX semantics
/// as the user-facing budget status. A restricted member never receives a whole-space threshold that
/// could reveal activity on an account they cannot see.
/// </summary>
public sealed class BudgetNotificationReconciliationService(FullWorthDbContext db)
{
    public async Task<ReconciledBudgetNotificationSignal?> EvaluateForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid budgetId,
        DateOnly asOf,
        CancellationToken ct)
    {
        var budget = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(b =>
            b.Id == budgetId && b.FullWorthSpaceId == fullWorthSpaceId && b.IsActive, ct);
        if (budget is null) return null;
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;

        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var allActiveAccounts = await db.Accounts.AsNoTracking()
            .Where(a => a.FullWorthSpaceId == fullWorthSpaceId && a.IsActive)
            .Select(a => a.Id).ToListAsync(ct);
        var scope = await LoadScope(budget, ct);
        var effectiveAccounts = scope.AccountIds.Count == 0
            ? visible.ToHashSet()
            : scope.AccountIds.Where(visible.Contains).ToHashSet();
        var partialAccess = scope.AccountIds.Count == 0
            ? allActiveAccounts.Any(id => !visible.Contains(id))
            : scope.AccountIds.Any(id => !visible.Contains(id));

        var period = BudgetCycleCalculator.CurrentPeriod(
            BudgetCycleResolver.Resolve(budget.Period, budget.StartDate, budget.EndDate), asOf);
        var converter = new CurrencyConverter(db);
        var reconciliation = new FinancialReconciliationService(db, converter);
        var loaded = await reconciliation.LoadAsync(
            userId, fullWorthSpaceId, period.Start, period.End, budget.Currency, effectiveAccounts,
            includeTransfers: false, includePending: false, includeIgnored: false, refundMode: "reverse", ct);
        if (loaded is null) return null;

        var categories = await ExpandCategories(fullWorthSpaceId, scope.Categories, ct);
        var merchants = scope.Merchants.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = scope.TagIds.ToHashSet();
        var relevant = loaded.Items.Where(item =>
            item.Kind is ContributionKinds.Expense or ContributionKinds.Refund &&
            (categories.Count == 0 || (item.CategoryId.HasValue && categories.Contains(item.CategoryId.Value))) &&
            (merchants.Count == 0 || merchants.Contains(item.Merchant)) &&
            (tags.Count == 0 || item.TagIds.Overlaps(tags)));
        var spent = FinancialReconciliationService.Spend(relevant);
        var percent = budget.Amount == 0m ? 0m : Math.Round(spent / budget.Amount * 100m, 2);
        var (near, critical) = await LoadThresholds(budgetId, ct);

        return new ReconciledBudgetNotificationSignal(
            budget.Id, budget.Name, percent, period.Start, near, critical, partialAccess, loaded.IncompleteFx);
    }

    private async Task<BudgetScope> LoadScope(Budget budget, CancellationToken ct)
    {
        var categories = new List<(Guid Id, bool Descendants)>();
        var accounts = new List<Guid>();
        var tags = new List<Guid>();
        var merchants = new List<string>();
        var connection = await ParitySql.OpenAsync(db, ct);

        await using (var command = ParitySql.Command(connection,
            "SELECT \"CategoryId\",\"IncludeDescendants\" FROM \"BudgetCategories\" WHERE \"BudgetId\"=@id", ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                categories.Add((ParitySql.Guid(reader, "CategoryId"), ParitySql.Bool(reader, "IncludeDescendants")));
        if (categories.Count == 0 && budget.CategoryId.HasValue)
            categories.Add((budget.CategoryId.Value, false));

        await using (var command = ParitySql.Command(connection,
            "SELECT \"AccountId\" FROM \"BudgetAccounts\" WHERE \"BudgetId\"=@id", ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) accounts.Add(ParitySql.Guid(reader, "AccountId"));

        await using (var command = ParitySql.Command(connection,
            "SELECT \"TagId\" FROM \"BudgetTags\" WHERE \"BudgetId\"=@id", ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tags.Add(ParitySql.Guid(reader, "TagId"));

        await using (var command = ParitySql.Command(connection,
            "SELECT \"NormalizedMerchant\" FROM \"BudgetMerchants\" WHERE \"BudgetId\"=@id", ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) merchants.Add(ParitySql.String(reader, "NormalizedMerchant"));

        return new BudgetScope(categories, accounts, tags, merchants);
    }

    private async Task<HashSet<Guid>> ExpandCategories(
        Guid fullWorthSpaceId,
        IReadOnlyList<(Guid Id, bool Descendants)> scopes,
        CancellationToken ct)
    {
        var result = scopes.Select(s => s.Id).ToHashSet();
        var roots = scopes.Where(s => s.Descendants).Select(s => s.Id).ToHashSet();
        if (roots.Count == 0) return result;
        var rows = await db.Categories.AsNoTracking().Where(c => c.FullWorthSpaceId == fullWorthSpaceId)
            .Select(c => new { c.Id, c.ParentId }).ToListAsync(ct);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var row in rows)
            {
                if (row.ParentId.HasValue && roots.Contains(row.ParentId.Value) && roots.Add(row.Id))
                {
                    result.Add(row.Id);
                    changed = true;
                }
            }
        }
        return result;
    }

    private async Task<(decimal Near, decimal Critical)> LoadThresholds(Guid budgetId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT \"AlertNearPercent\",\"AlertCriticalPercent\" FROM \"BudgetAdvancedSettings\" WHERE \"BudgetId\"=@id", ("@id", budgetId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (80m, 100m);
        var near = Math.Max(0m, ParitySql.Decimal(reader, "AlertNearPercent"));
        var critical = Math.Max(near, ParitySql.Decimal(reader, "AlertCriticalPercent"));
        return (near, critical);
    }

    private sealed record BudgetScope(
        List<(Guid Id, bool Descendants)> Categories,
        List<Guid> AccountIds,
        List<Guid> TagIds,
        List<string> Merchants);
}
