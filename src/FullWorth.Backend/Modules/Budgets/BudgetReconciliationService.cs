using FullWorth.Backend.Modules.Budgets.CarryOver;
using FullWorth.Backend.Modules.Reconciliation;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Budgets.Cycles;
using FullWorth.Backend.Modules.Budgets.Forecast;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Budgets;

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
    IReadOnlyList<ReconciledBudgetContribution> Contributing)
{
    /// <summary>Der Grundbetrag ohne Uebertrag. <c>BudgetAmount</c> ist der effektive.</summary>
    public decimal BaseBudgetAmount { get; init; }

    /// <summary>Was aus abgeschlossenen Perioden hereingetragen wird; negativ bei Ueberziehung.</summary>
    public decimal CarryIn { get; init; }

    public bool CarryOver { get; init; }
    public bool CarryOverOverspend { get; init; }
}

/// <summary>
/// Der Budgetstand - und zwar der einzige.
///
/// Die Datei hiess bis 2026-09-23 "BudgetReconciliationCompatibility" und enthielt neben diesem
/// Dienst eine Middleware, die die drei oeffentlichen Status-Routen VOR der Zuordnung abfing und aus
/// ihm beantwortete. Die eigentlich gemappten Handler liefen nie - und weil sie trotzdem dastanden,
/// gruen getestet und mit Aufrufern im Frontend, sah nichts danach aus, als waere etwas falsch.
/// Gezaehlt waren es fuenf Fassungen derselben Frage; vier davon kamen nie an.
///
/// Jetzt rufen die Handler hier direkt herein. Wer etwas am Budgetstand aendert, aendert es hier,
/// und es erreicht die Budgetseite, die Buchungsseite und den Coach zugleich.
/// </summary>
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
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var budget = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == budgetId && row.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (budget is null) return null;

        var visible = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
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
        var cycle = BudgetCycleResolver.Resolve(budget.Period, budget.StartDate, budget.EndDate);
        var period = BudgetCycleCalculator.CurrentPeriod(cycle, day);

        var categoryIds = await ExpandCategoriesAsync(fullWorthSpaceId, scope.Categories, ct);
        var merchants = scope.Merchants.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = scope.TagIds.ToHashSet();

        // Der Bereich des Budgets, auf eine geladene Spanne angewandt. Als eigene Funktion, weil die
        // Historie fuer den Uebertrag GENAU denselben Filter braucht - waere er dort auch nur um eine
        // Bedingung anders, zeigte die laufende Periode etwas anderes als die, aus der sie erbt.
        List<CanonicalContribution> Relevant(IEnumerable<CanonicalContribution> items) => items.Where(item =>
            item.Kind is ContributionKinds.Expense or ContributionKinds.Refund &&
            (categoryIds.Count == 0 || (item.CategoryId.HasValue && categoryIds.Contains(item.CategoryId.Value))) &&
            (merchants.Count == 0 || merchants.Contains(item.Merchant)) &&
            (tags.Count == 0 || item.TagIds.Overlaps(tags))).ToList();

        Task<CanonicalContributionLoad?> LoadAsync(DateOnly from, DateOnly to) => reconciliation.LoadAsync(
            userId,
            fullWorthSpaceId,
            from,
            to,
            budget.Currency,
            effectiveAccounts,
            includeTransfers: false,
            includePending: false,
            includeIgnored: false,
            refundMode: "reverse",
            ct);

        var loaded = await LoadAsync(period.Start, period.End);
        if (loaded is null) return null;

        var relevant = Relevant(loaded.Items);
        var spent = FinancialReconciliationService.Spend(relevant);
        var incompleteFx = loaded.IncompleteFx;

        // Der Uebertrag (#115). Er stand im BudgetStore fertig da, aber diese Klasse bedient die drei
        // oeffentlichen Status-Routen - ohne ihn zeigte ein Budget mit Uebertrag ueberall den nackten
        // Grundbetrag, und die Einstellung war folgenlos.
        var carryIn = 0m;
        var carryMode = BudgetCarryOverWindow.Mode(budget);
        if (carryMode != CarryOverMode.Disabled)
        {
            var activeFrom = BudgetCarryOverWindow.ActiveFrom(budget, cycle, day);
            var priorPeriods = BudgetCarryOverWindow.PriorPeriods(cycle, activeFrom, period);
            if (priorPeriods.Count > 0)
            {
                var historyFrom = activeFrom > priorPeriods[0].Start ? activeFrom : priorPeriods[0].Start;
                var history = await LoadAsync(historyFrom, period.Start.AddDays(-1));
                if (history is not null)
                {
                    // Ein fehlender Kurs in der Historie macht auch den Uebertrag unvollstaendig -
                    // und damit jede Zahl, die auf ihm steht. Das gehoert weitergereicht.
                    incompleteFx |= history.IncompleteFx;
                    var spentByPeriod = Relevant(history.Items)
                        .GroupBy(item => BudgetCycleCalculator.CurrentPeriod(cycle, item.Date).Start)
                        .ToDictionary(group => group.Key, FinancialReconciliationService.Spend);
                    var priorSpends = priorPeriods
                        .Select(previous => spentByPeriod.GetValueOrDefault(previous.Start))
                        .ToList();
                    carryIn = BudgetCarryOverCalculator.CarriedIn(carryMode, budget.Amount, priorSpends);
                }
            }
        }

        var effectiveAmount = budget.Amount + carryIn;
        var totalDays = period.LengthInDays;
        var elapsedDays = Math.Clamp(day.DayNumber - period.Start.DayNumber + 1, 0, totalDays);
        var forecast = BudgetForecastCalculator.Project(new BudgetForecastInput(
            effectiveAmount,
            spent,
            totalDays,
            elapsedDays,
            HistoricalDailyAverage: null));
        var percent = BudgetCarryOverWindow.PercentUsed(effectiveAmount, spent);

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
            effectiveAmount,
            spent,
            effectiveAmount - spent,
            percent,
            forecast.ProjectedEndSpend,
            forecast.ProjectedOverUnder,
            forecast.Trend.ToString(),
            partialAccess,
            incompleteFx,
            contributions)
        {
            BaseBudgetAmount = budget.Amount,
            CarryIn = carryIn,
            CarryOver = budget.CarryOver,
            CarryOverOverspend = budget.CarryOverOverspend
        };
    }

    public async Task<object?> GetListAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        int? year,
        int? month,
        string? currency,
        CancellationToken ct)
    {
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
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

        // Zu welcher Gruppe ein Budget gehoert, steht in BudgetAdvancedSettings. Eine Abfrage fuer
        // alle, nicht eine je Budget: die Schleife darunter ruft ohnehin schon GetStatusAsync je
        // Budget auf, und noch eine Runde je Budget waere genau die Art N+1, die in diesem Haus
        // schon fuenfmal gefunden wurde.
        var groupOfBudget = new Dictionary<Guid, Guid>();
        if (budgets.Count > 0)
        {
            var connection = await RawSql.OpenAsync(db, ct);
            await using var command = RawSql.Command(connection,
                "SELECT \"BudgetId\",\"GroupId\" FROM \"BudgetAdvancedSettings\" WHERE \"BudgetId\"=ANY(@ids) AND \"GroupId\" IS NOT NULL",
                ("@ids", budgets.Select(budget => budget.Id).ToArray()));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                groupOfBudget[RawSql.Guid(reader, "BudgetId")] = RawSql.Guid(reader, "GroupId");
        }

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
                groupId = groupOfBudget.TryGetValue(status.BudgetId, out var group) ? group : (Guid?)null,
                status.CategoryId,
                status.Period,
                status.PeriodStart,
                status.PeriodEnd,
                amount = status.BudgetAmount,
                status.Spent,
                status.Remaining,
                percent = status.PercentUsed,
                status.PartialAccess,
                status.BaseBudgetAmount,
                status.CarryIn,
                status.CarryOver
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
        var connection = await RawSql.OpenAsync(db, ct);

        await using (var command = RawSql.Command(connection,
            "SELECT \"CategoryId\",\"IncludeDescendants\" FROM \"BudgetCategories\" WHERE \"BudgetId\"=@id",
            ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                categories.Add(new CategoryScope(
                    RawSql.Guid(reader, "CategoryId"),
                    RawSql.Bool(reader, "IncludeDescendants")));

        // Legacy budgets stored one exact category directly on Budgets.CategoryId. Only use this when
        // no explicit advanced category scope exists, and preserve the old exact-match semantics.
        if (categories.Count == 0 && budget.CategoryId.HasValue)
            categories.Add(new CategoryScope(budget.CategoryId.Value, IncludeDescendants: false));

        await using (var command = RawSql.Command(connection,
            "SELECT \"AccountId\" FROM \"BudgetAccounts\" WHERE \"BudgetId\"=@id", ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) accounts.Add(RawSql.Guid(reader, "AccountId"));

        await using (var command = RawSql.Command(connection,
            "SELECT \"TagId\" FROM \"BudgetTags\" WHERE \"BudgetId\"=@id", ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tags.Add(RawSql.Guid(reader, "TagId"));

        await using (var command = RawSql.Command(connection,
            "SELECT \"NormalizedMerchant\" FROM \"BudgetMerchants\" WHERE \"BudgetId\"=@id", ("@id", budget.Id)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) merchants.Add(RawSql.String(reader, "NormalizedMerchant"));

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
