using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Budgets.Cycles;
using FullWorth.Backend.Modules.Budgets.Forecast;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record ReconciledBudgetContribution(
    Guid TransactionId,
    DateOnly BookingDate,
    string Counterparty,
    decimal Amount,
    string Currency,
    Guid? CategoryId,
    string? Category,
    string Kind);

public sealed record ReconciledBudgetStatus(
    Guid BudgetId,
    string Name,
    Guid? CategoryId,
    string Currency,
    string Period,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal BudgetAmount,
    decimal Spent,
    decimal Remaining,
    decimal PercentUsed,
    decimal ProjectedEndSpend,
    decimal ProjectedOverUnder,
    string Trend,
    bool PartialAccess,
    bool IncompleteFx,
    IReadOnlyList<ReconciledBudgetContribution> Contributing);

public sealed class BudgetReconciliationService(
    FullWorthDbContext db,
    FinancialReconciliationService reconciliation)
{
    public async Task<ReconciledBudgetStatus?> GetStatusAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid budgetId,
        DateOnly? asOf,
        CancellationToken ct)
    {
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var budget = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == budgetId && row.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (budget is null) return null;

        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var allActiveAccounts = await db.Accounts.AsNoTracking()
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.IsActive)
            .Select(account => account.Id).ToListAsync(ct);
        var scope = await LoadScopeAsync(budget, ct);
        var effectiveAccounts = scope.AccountIds.Count == 0
            ? visible.ToHashSet()
            : scope.AccountIds.Where(visible.Contains).ToHashSet();
        var partialAccess = scope.AccountIds.Count == 0
            ? allActiveAccounts.Any(id => !visible.Contains(id))
            : scope.AccountIds.Any(id => !visible.Contains(id));

        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var period = BudgetCycleCalculator.CurrentPeriod(
            BudgetCycleResolver.Resolve(budget.Period, budget.StartDate, budget.EndDate), day);
        var loaded = await reconciliation.LoadAsync(
            userId,
            fullWorthSpaceId,
            period.Start,
            period.End,
            budget.Currency,
            effectiveAccounts,
            includeTransfers: false,
            includePending: false,
            includeIgnored: false,
            refundMode: "reverse",
            ct);
        if (loaded is null) return null;

        var categoryIds = await ExpandCategoriesAsync(fullWorthSpaceId, scope.Categories, ct);
        var merchants = scope.Merchants.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = scope.TagIds.ToHashSet();
        var relevant = loaded.Items.Where(item =>
            item.Kind is ContributionKinds.Expense or ContributionKinds.Refund &&
            (categoryIds.Count == 0 || (item.CategoryId.HasValue && categoryIds.Contains(item.CategoryId.Value))) &&
            (merchants.Count == 0 || merchants.Contains(item.Merchant)) &&
            (tags.Count == 0 || item.TagIds.Overlaps(tags))).ToList();

        var spent = FinancialReconciliationService.Spend(relevant);
        var totalDays = period.LengthInDays;
        var elapsedDays = Math.Clamp(day.DayNumber - period.Start.DayNumber + 1, 0, totalDays);
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            budget.Amount,
            spent,
            totalDays,
            elapsedDays,
            HistoricalDailyAverage: null));
        var percent = budget.Amount == 0m
            ? 0m
            : Math.Round(spent / budget.Amount * 100m, 2, MidpointRounding.AwayFromZero);

        var categoryNames = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(category => category.Id, category => category.Name, ct);
        var contributions = relevant
            .OrderByDescending(item => item.Date)
            .ThenBy(item => item.TransactionId)
            .Take(200)
            .Select(item => new ReconciledBudgetContribution(
                item.TransactionId,
                item.Date,
                item.Merchant,
                Math.Round(item.ReportingAmount, 2, MidpointRounding.AwayFromZero),
                budget.Currency,
                item.CategoryId,
                item.CategoryId.HasValue ? categoryNames.GetValueOrDefault(item.CategoryId.Value) : null,
                item.Kind))
            .ToArray();

        return new ReconciledBudgetStatus(
            budget.Id,
            budget.Name,
            budget.CategoryId,
            budget.Currency,
            budget.Period,
            period.Start,
            period.End,
            budget.Amount,
            spent,
            budget.Amount - spent,
            percent,
            forecast.ProjectedEndSpend,
            forecast.ProjectedOverUnder,
            forecast.Trend.ToString(),
            partialAccess,
            loaded.IncompleteFx,
            contributions);
    }

    public async Task<object?> GetListAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        int? year,
        int? month,
        string? currency,
        CancellationToken ct)
    {
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var space = await db.FullWorthSpaces.AsNoTracking().SingleOrDefaultAsync(row => row.Id == fullWorthSpaceId, ct);
        if (space is null) return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var resolvedYear = year ?? today.Year;
        var resolvedMonth = month ?? today.Month;
        if (resolvedMonth is < 1 or > 12) throw new ArgumentException("month must be between 1 and 12.");
        if (resolvedYear is < 1 or > 9999) throw new ArgumentException("year is invalid.");
        var reportingCurrency = string.IsNullOrWhiteSpace(currency)
            ? space.BaseCurrency
            : currency.Trim().ToUpperInvariant();
        var reference = resolvedYear == today.Year && resolvedMonth == today.Month
            ? today
            : new DateOnly(resolvedYear, resolvedMonth, 1);

        var budgets = await db.Budgets.AsNoTracking()
            .Where(budget => budget.FullWorthSpaceId == fullWorthSpaceId && budget.IsActive && budget.Currency == reportingCurrency)
            .OrderBy(budget => budget.Name)
            .Select(budget => new { budget.Id })
            .ToListAsync(ct);

        var items = new List<object>(budgets.Count);
        var incomplete = false;
        foreach (var budget in budgets)
        {
            var status = await GetStatusAsync(userId, fullWorthSpaceId, budget.Id, reference, ct);
            if (status is null) continue;
            incomplete |= status.IncompleteFx;
            items.Add(new
            {
                id = status.BudgetId,
                status.Name,
                status.CategoryId,
                status.Period,
                status.PeriodStart,
                status.PeriodEnd,
                amount = status.BudgetAmount,
                status.Spent,
                status.Remaining,
                percent = status.PercentUsed,
                status.PartialAccess
            });
        }

        return new
        {
            year = resolvedYear,
            month = resolvedMonth,
            currency = reportingCurrency,
            items,
            incomplete
        };
    }

    private async Task<BudgetScope> LoadScopeAsync(Budget budget, CancellationToken ct)
    {
        var categories = new List<CategoryScope>();
        var accounts = new List<Guid>();
        var tags = new List<Guid>();
        var merchants = new List<string>();
        var connection = await ParitySql.OpenAsync(db, ct);

        await using (var command = ParitySql.Command(connection,
            "SELECT \"CategoryId\",\"IncludeDescendants\" FROM \"BudgetCategories\" WHERE \"BudgetId\"=@id",
            ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                categories.Add(new CategoryScope(
                    ParitySql.Guid(reader, "CategoryId"),
                    ParitySql.Bool(reader, "IncludeDescendants")));

        // Legacy budgets stored one exact category directly on Budgets.CategoryId. Only use this when
        // no explicit advanced category scope exists, and preserve the old exact-match semantics.
        if (categories.Count == 0 && budget.CategoryId.HasValue)
            categories.Add(new CategoryScope(budget.CategoryId.Value, IncludeDescendants: false));

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

    private async Task<HashSet<Guid>> ExpandCategoriesAsync(
        Guid fullWorthSpaceId,
        IReadOnlyList<CategoryScope> scopes,
        CancellationToken ct)
    {
        var result = scopes.Select(scope => scope.Id).ToHashSet();
        var roots = scopes.Where(scope => scope.IncludeDescendants).Select(scope => scope.Id).ToHashSet();
        if (roots.Count == 0) return result;

        var rows = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .Select(category => new { category.Id, category.ParentId })
            .ToListAsync(ct);
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

    private sealed record CategoryScope(Guid Id, bool IncludeDescendants);
    private sealed record BudgetScope(
        List<CategoryScope> Categories,
        List<Guid> AccountIds,
        List<Guid> TagIds,
        List<string> Merchants);
}

/// <summary>
/// Keeps the original budget URLs/JSON contracts while routing every budget surface through one
/// canonical reconciliation service. This covers the main budget list/detail and the newer scope view.
/// </summary>
public sealed class BudgetReconciliationCompatibilityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        CurrentUserContext currentUser,
        BudgetReconciliationService budgets)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var isList = path.Equals("/api/analytics/budget-status", StringComparison.OrdinalIgnoreCase);
        var isAdvanced = TryIdPath(path, "/api/budget-scopes/", "/status", out var advancedId);
        var isLegacyDetail = TryIdPath(path, "/api/budgets/", "/status", out var legacyId);
        if (!isList && !isAdvanced && !isLegacyDetail)
        {
            await next(context);
            return;
        }

        var ct = context.RequestAborted;
        try
        {
            var userId = currentUser.RequireUserId();
            var fullWorthSpaceId = RequiredSpace(context);
            if (isList)
            {
                var result = await budgets.GetListAsync(
                    userId,
                    fullWorthSpaceId,
                    OptionalInt(context, "year"),
                    OptionalInt(context, "month"),
                    context.Request.Query["currency"].FirstOrDefault(),
                    ct);
                await Write(context, result, ct);
                return;
            }

            var status = await budgets.GetStatusAsync(
                userId,
                fullWorthSpaceId,
                isAdvanced ? advancedId : legacyId,
                OptionalDate(context, "asOf"),
                ct);
            if (status is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (isAdvanced)
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    status.BudgetId,
                    status.Name,
                    amount = status.BudgetAmount,
                    status.Currency,
                    status.PeriodStart,
                    status.PeriodEnd,
                    status.Spent,
                    status.Remaining,
                    status.PercentUsed,
                    status.ProjectedEndSpend,
                    status.ProjectedOverUnder,
                    status.PartialAccess,
                    incompleteFx = status.IncompleteFx,
                    status.Contributing
                }, cancellationToken: ct);
                return;
            }

            await context.Response.WriteAsJsonAsync(status, cancellationToken: ct);
        }
        catch (UnauthorizedAccessException exception)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message }, cancellationToken: ct);
        }
        catch (ArgumentException exception)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message }, cancellationToken: ct);
        }
    }

    private static Guid RequiredSpace(HttpContext context)
    {
        if (Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var id)) return id;
        throw new ArgumentException("fullWorthSpaceId is required.");
    }

    private static DateOnly? OptionalDate(HttpContext context, string name) =>
        DateOnly.TryParse(context.Request.Query[name], out var value) ? value : null;

    private static int? OptionalInt(HttpContext context, string name) =>
        int.TryParse(context.Request.Query[name], out var value) ? value : null;

    private static bool TryIdPath(string path, string prefix, string suffix, out Guid id)
    {
        id = Guid.Empty;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var raw = path[prefix.Length..^suffix.Length].Trim('/');
        return Guid.TryParse(raw, out id);
    }

    private static async Task Write(HttpContext context, object? value, CancellationToken ct)
    {
        if (value is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await context.Response.WriteAsJsonAsync(value, cancellationToken: ct);
    }
}
