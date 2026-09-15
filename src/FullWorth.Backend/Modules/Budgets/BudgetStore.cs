using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Budgets.CarryOver;
using FullWorth.Backend.Modules.Budgets.Cycles;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using FullWorth.Backend.Validation;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Budgets;

public sealed class BudgetStore(FullWorthDbContext db, AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    public Task<List<Budget>> ListAsync(CancellationToken ct) => ListForSpaceAsync(FullWorthSpaceDefaults.LegacyId, ct);

    public Task<List<Budget>> ListForSpaceAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Budgets.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId).OrderBy(x => x.Name).ToListAsync(ct);

    public Task<Budget> UpsertAsync(Guid? id, BudgetWrite request, CancellationToken ct) =>
        UpsertForSpaceAsync(FullWorthSpaceDefaults.LegacyId, id, request, ct);

    public async Task<Budget> UpsertForSpaceAsync(Guid fullWorthSpaceId, Guid? id, BudgetWrite request, CancellationToken ct)
    {
        if (request.CategoryId.HasValue &&
            !await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct))
            throw new InvalidOperationException("Budget category must belong to the same FullWorth Space.");

        var entity = id.HasValue
            ? await db.Budgets.SingleOrDefaultAsync(x => x.Id == id.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct)
            : null;
        if (id.HasValue && entity is null) throw new InvalidOperationException("Budget not found in FullWorth Space.");
        if (entity is null)
        {
            entity = new Budget { FullWorthSpaceId = fullWorthSpaceId };
            db.Budgets.Add(entity);
        }
        ApplyWrite(entity, request);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public Task<List<BudgetView>> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        Project(VisibleBudgets(userId, fullWorthSpaceId).OrderBy(budget => budget.Name)).ToListAsync(ct);

    public Task<BudgetView?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid budgetId, CancellationToken ct) =>
        Project(VisibleBudgets(userId, fullWorthSpaceId).Where(budget => budget.Id == budgetId)).SingleOrDefaultAsync(ct);

    /// <summary>Space-level percent-used per active budget for its current cycle (no user/visibility
    /// filter). Used by the post-sync budget-threshold notifications, which are space-scoped.</summary>
    public async Task<List<BudgetSignal>> GetSpaceBudgetSignalsAsync(Guid fullWorthSpaceId, DateOnly asOf, CancellationToken ct)
    {
        var budgets = await db.Budgets.AsNoTracking()
            .Where(budget => budget.FullWorthSpaceId == fullWorthSpaceId && budget.IsActive)
            .ToListAsync(ct);

        var signals = new List<BudgetSignal>(budgets.Count);
        foreach (var budget in budgets)
        {
            var cycle = ResolveCycle(budget);
            var period = BudgetCycleCalculator.CurrentPeriod(cycle, asOf);

            IQueryable<FinanceTransaction> ExpensesBetween(DateOnly from, DateOnly to)
            {
                var query = db.Transactions.AsNoTracking().Where(transaction =>
                    !transaction.IsIgnored &&
                    !transaction.IsTransfer &&
                    transaction.Amount < 0 &&
                    transaction.BookingDate != null &&
                    transaction.BookingDate >= from &&
                    transaction.BookingDate <= to &&
                    db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId));
                return budget.CategoryId.HasValue
                    ? query.Where(transaction => transaction.CategoryId == budget.CategoryId.Value)
                    : query;
            }

            var spent = -(await ExpensesBetween(period.Start, period.End)
                .SumAsync(transaction => (decimal?)transaction.Amount, ct) ?? 0m);

            var carryIn = 0m;
            var carryMode = ResolveCarryMode(budget);
            if (carryMode != CarryOverMode.Disabled)
            {
                var activeFrom = BudgetActiveFrom(budget, cycle);
                var priorPeriods = PriorPeriods(cycle, activeFrom, period);
                if (priorPeriods.Count > 0)
                {
                    var historyFrom = activeFrom > priorPeriods[0].Start ? activeFrom : priorPeriods[0].Start;
                    var historyTo = period.Start.AddDays(-1);
                    var history = await ExpensesBetween(historyFrom, historyTo)
                        .Select(transaction => new { Date = transaction.BookingDate!.Value, transaction.Amount })
                        .ToListAsync(ct);
                    var spentByPeriod = history
                        .GroupBy(row => BudgetCycleCalculator.CurrentPeriod(cycle, row.Date).Start)
                        .ToDictionary(group => group.Key, group => -group.Sum(row => row.Amount));
                    var priorSpends = priorPeriods
                        .Select(previous => spentByPeriod.GetValueOrDefault(previous.Start))
                        .ToList();
                    carryIn = BudgetCarryOverCalculator.CarriedIn(carryMode, budget.Amount, priorSpends);
                }
            }

            var effectiveAmount = budget.Amount + carryIn;
            var percentUsed = CalculatePercentUsed(effectiveAmount, spent);
            signals.Add(new BudgetSignal(budget.Id, budget.Name, percentUsed, period.Start));
        }
        return signals;
    }

    /// <summary>
    /// Budget-vs-actual for the cycle window that contains <paramref name="asOf"/>. User-facing
    /// calculations are restricted to accounts the caller can actually see. A partial-access flag
    /// makes it explicit when the result cannot represent the whole FullWorth Space.
    /// </summary>
    public async Task<BudgetPeriodStatus?> GetStatusForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid budgetId, DateOnly asOf, CancellationToken ct)
    {
        var budget = await VisibleBudgets(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == budgetId, ct);
        if (budget is null) return null;

        var visibleAccountIds = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var allAccountIds = await db.Accounts.AsNoTracking()
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.IsActive)
            .Select(account => account.Id)
            .ToListAsync(ct);
        var partialAccess = allAccountIds.Any(accountId => !visibleAccountIds.Contains(accountId));

        var cycle = ResolveCycle(budget);
        var period = BudgetCycleCalculator.CurrentPeriod(cycle, asOf);

        IQueryable<FinanceTransaction> ExpensesIn(BudgetCyclePeriod window)
        {
            var query = db.Transactions.AsNoTracking().Where(transaction =>
                visibleAccountIds.Contains(transaction.AccountId) &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.Amount < 0 &&
                transaction.BookingDate != null &&
                transaction.BookingDate >= window.Start &&
                transaction.BookingDate <= window.End);
            return budget.CategoryId.HasValue
                ? query.Where(transaction => transaction.CategoryId == budget.CategoryId.Value)
                : query;
        }

        var carryIn = 0m;
        var carryMode = ResolveCarryMode(budget);
        if (carryMode != CarryOverMode.Disabled)
        {
            var activeFrom = BudgetActiveFrom(budget, cycle);
            var priorPeriods = PriorPeriods(cycle, activeFrom, period);
            if (priorPeriods.Count > 0)
            {
                var historyFrom = activeFrom > priorPeriods[0].Start ? activeFrom : priorPeriods[0].Start;
                var history = await ExpensesIn(new BudgetCyclePeriod(historyFrom, period.Start.AddDays(-1)))
                    .Select(transaction => new { Date = transaction.BookingDate!.Value, transaction.Amount })
                    .ToListAsync(ct);
                var spentByPeriod = history
                    .GroupBy(row => BudgetCycleCalculator.CurrentPeriod(cycle, row.Date).Start)
                    .ToDictionary(group => group.Key, group => -group.Sum(row => row.Amount));
                var priorSpends = priorPeriods
                    .Select(previous => spentByPeriod.GetValueOrDefault(previous.Start))
                    .ToList();
                carryIn = BudgetCarryOverCalculator.CarriedIn(carryMode, budget.Amount, priorSpends);
            }
        }

        var effectiveBudget = budget.Amount + carryIn;
        var spent = -(await ExpensesIn(period).SumAsync(transaction => (decimal?)transaction.Amount, ct) ?? 0m);
        var remaining = effectiveBudget - spent;
        var percentUsed = CalculatePercentUsed(effectiveBudget, spent);

        var previous = BudgetCycleCalculator.PreviousPeriod(cycle, asOf);
        var previousSpent = -(await ExpensesIn(previous).SumAsync(transaction => (decimal?)transaction.Amount, ct) ?? 0m);
        var previousDays = previous.End.DayNumber - previous.Start.DayNumber + 1;
        decimal? historicalDaily = previousSpent > 0m && previousDays > 0 ? previousSpent / previousDays : null;
        var totalDays = period.End.DayNumber - period.Start.DayNumber + 1;
        var elapsedDays = Math.Clamp(asOf.DayNumber - period.Start.DayNumber + 1, 0, totalDays);
        var forecast = Forecast.BudgetForecastCalculator.Project(
            new Forecast.BudgetForecastInput(effectiveBudget, spent, totalDays, elapsedDays, historicalDaily));

        var contributing = await ExpensesIn(period)
            .OrderByDescending(transaction => transaction.BookingDate)
            .ThenByDescending(transaction => transaction.UpdatedAt)
            .Take(100)
            .Select(transaction => new BudgetContributionRow(
                transaction.Id,
                transaction.BookingDate,
                transaction.Counterparty,
                transaction.Amount,
                transaction.Currency,
                db.Categories.Where(category => category.Id == transaction.CategoryId).Select(category => category.Name).FirstOrDefault()))
            .ToListAsync(ct);

        return new BudgetPeriodStatus(
            budget.Id, budget.Name, budget.CategoryId, budget.Currency, budget.Period,
            period.Start, period.End, effectiveBudget, spent, remaining, percentUsed,
            forecast.ProjectedEndSpend, forecast.ProjectedOverUnder, forecast.Trend.ToString(), partialAccess, contributing)
        {
            BaseBudgetAmount = budget.Amount,
            CarryIn = carryIn,
            CarryOver = budget.CarryOver,
            CarryOverOverspend = budget.CarryOverOverspend
        };
    }

    private static BudgetCycleDefinition ResolveCycle(Budget budget) =>
        BudgetCycleResolver.Resolve(budget.Period, budget.StartDate, budget.EndDate);

    private static CarryOverMode ResolveCarryMode(Budget budget) =>
        !budget.CarryOver
            ? CarryOverMode.Disabled
            : budget.CarryOverOverspend ? CarryOverMode.Enabled : CarryOverMode.PositiveOnly;

    /// <summary>
    /// Ab wann der Uebertrag zaehlt. Das ist NICHT der Periodenbeginn: die Periode sagt, wie lang ein
    /// Fenster ist, diese Angabe sagt, ab welchem Fenster ueberhaupt gerechnet wird (#115).
    ///
    /// Ohne Angabe bleibt es beim bisherigen Verhalten - ab dem Startdatum, sonst ab der Anlage.
    /// </summary>
    private static DateOnly BudgetActiveFrom(Budget budget, BudgetCycleDefinition cycle)
    {
        var current = BudgetCycleCalculator.CurrentPeriod(cycle, DateOnly.FromDateTime(DateTime.UtcNow));
        switch (budget.CarryOverStart)
        {
            // Nur diese Periode: aeltere Historie beeinflusst den Uebertrag nicht.
            case "this-period": return current.Start;
            case "from-date" when budget.CarryOverFrom is { } chosen:
                return BudgetCycleCalculator.CurrentPeriod(cycle, chosen).Start;
        }
        if (budget.StartDate is { } explicitStart) return explicitStart;
        var created = DateOnly.FromDateTime(budget.CreatedAt.UtcDateTime);
        return BudgetCycleCalculator.CurrentPeriod(cycle, created).Start;
    }

    private static List<BudgetCyclePeriod> PriorPeriods(
        BudgetCycleDefinition cycle,
        DateOnly activeFrom,
        BudgetCyclePeriod current)
    {
        if (activeFrom >= current.Start) return [];

        var periods = new List<BudgetCyclePeriod>();
        var cursor = BudgetCycleCalculator.CurrentPeriod(cycle, activeFrom);
        var guard = 0;
        while (cursor.Start < current.Start && guard++ < 20000)
        {
            periods.Add(cursor);
            cursor = BudgetCycleCalculator.CurrentPeriod(cycle, cursor.EndExclusive);
        }
        return periods;
    }

    private static decimal CalculatePercentUsed(decimal effectiveBudget, decimal spent)
    {
        if (effectiveBudget > 0m)
            return Math.Round(spent / effectiveBudget * 100m, 2, MidpointRounding.AwayFromZero);
        return spent > 0m || effectiveBudget < 0m ? 101m : 0m;
    }

    public async Task<BudgetAccessLevel> GetAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid budgetId, CancellationToken ct)
    {
        var visible = await VisibleBudgets(userId, fullWorthSpaceId).AnyAsync(budget => budget.Id == budgetId, ct);
        if (!visible) return BudgetAccessLevel.None;
        return await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "budgets.manage", ct)
            ? BudgetAccessLevel.Write
            : BudgetAccessLevel.Read;
    }

    public async Task<BudgetMutationOutcome> CreateForUserAsync(Guid userId, Guid fullWorthSpaceId, BudgetWrite request, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(BudgetMutationResult.NotFound);
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "budgets.manage", ct))
            return new(BudgetMutationResult.Forbidden);

        if (!await CategoryIsValidAsync(fullWorthSpaceId, request.CategoryId, ct))
            return new(BudgetMutationResult.NotFound);
        var validationError = ValidateWrite(request);
        if (validationError is not null) return new(BudgetMutationResult.Invalid, Error: validationError);

        var entity = new Budget { FullWorthSpaceId = fullWorthSpaceId };
        ApplyWrite(entity, request);
        db.Budgets.Add(entity);
        audit.Record(fullWorthSpaceId, userId, "budget.created", "Budget", entity.Id);
        await db.SaveChangesAsync(ct);
        return new(BudgetMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, entity.Id, ct));
    }

    public async Task<BudgetMutationOutcome> UpdateForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid budgetId, BudgetWrite request, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, budgetId, ct);
        if (access == BudgetAccessLevel.None) return new(BudgetMutationResult.NotFound);
        if (access != BudgetAccessLevel.Write) return new(BudgetMutationResult.Forbidden);

        if (!await CategoryIsValidAsync(fullWorthSpaceId, request.CategoryId, ct))
            return new(BudgetMutationResult.NotFound);
        var validationError = ValidateWrite(request);
        if (validationError is not null) return new(BudgetMutationResult.Invalid, Error: validationError);

        var entity = await db.Budgets.SingleOrDefaultAsync(budget => budget.Id == budgetId && budget.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (entity is null) return new(BudgetMutationResult.NotFound);
        ApplyWrite(entity, request);
        audit.Record(fullWorthSpaceId, userId, "budget.updated", "Budget", entity.Id);
        await db.SaveChangesAsync(ct);
        return new(BudgetMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, budgetId, ct));
    }

    public async Task<BudgetMutationResult> ArchiveForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid budgetId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, budgetId, ct);
        if (access == BudgetAccessLevel.None) return BudgetMutationResult.NotFound;
        if (access != BudgetAccessLevel.Write) return BudgetMutationResult.Forbidden;

        var entity = await db.Budgets.SingleOrDefaultAsync(budget => budget.Id == budgetId && budget.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (entity is null) return BudgetMutationResult.NotFound;
        entity.IsActive = false;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "budget.archived", "Budget", entity.Id);
        await db.SaveChangesAsync(ct);
        return BudgetMutationResult.Success;
    }

    private IQueryable<Budget> VisibleBudgets(Guid userId, Guid fullWorthSpaceId) =>
        db.Budgets.AsNoTracking().Where(budget =>
            budget.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId));

    private IQueryable<BudgetView> Project(IQueryable<Budget> budgets) =>
        budgets.Select(budget => new BudgetView(
            budget.Id,
            budget.FullWorthSpaceId,
            budget.Name,
            budget.CategoryId,
            budget.Amount,
            budget.Currency,
            budget.Period,
            budget.CarryOver,
            budget.IsActive,
            budget.StartDate,
            budget.EndDate,
            budget.CreatedAt,
            budget.UpdatedAt)
        {
            CarryOverOverspend = budget.CarryOverOverspend,
            CarryOverStart = budget.CarryOverStart,
            CarryOverFrom = budget.CarryOverFrom
        });

    private Task<string?> GetSpaceRoleAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);

    private Task<bool> CategoryIsValidAsync(Guid fullWorthSpaceId, Guid? categoryId, CancellationToken ct) =>
        !categoryId.HasValue
            ? Task.FromResult(true)
            : db.Categories.AsNoTracking().AnyAsync(category => category.Id == categoryId.Value && category.FullWorthSpaceId == fullWorthSpaceId, ct);

    private static string? ValidateWrite(BudgetWrite request) =>
        Validate.RequiredName(request.Name, "Budget name")
        ?? Validate.Currency(request.Currency)
        ?? (string.IsNullOrWhiteSpace(request.Period) ? "Budget period is required." : null);

    private static void ApplyWrite(Budget entity, BudgetWrite request)
    {
        entity.Name = request.Name.Trim();
        entity.CategoryId = request.CategoryId;
        entity.Amount = request.Amount;
        entity.Currency = request.Currency.Trim().ToUpperInvariant();
        entity.Period = request.Period.Trim().ToLowerInvariant();
        entity.CarryOver = request.CarryOver;
        entity.CarryOverOverspend = request.CarryOver && (request.CarryOverOverspend ?? true);
        entity.CarryOverStart = string.IsNullOrWhiteSpace(request.CarryOverStart) ? null : request.CarryOverStart.Trim();
        entity.CarryOverFrom = request.CarryOverFrom;
        entity.IsActive = request.IsActive;
        entity.StartDate = request.StartDate;
        entity.EndDate = request.EndDate;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
