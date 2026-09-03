using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Budgets.Cycles;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Parity;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using FullWorth.Backend.Validation;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Budgets;

public sealed class Budget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Period { get; set; } = "monthly";
    public bool CarryOver { get; set; }
    public bool IsActive { get; set; } = true;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record BudgetView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    Guid? CategoryId,
    decimal Amount,
    string Currency,
    string Period,
    bool CarryOver,
    bool IsActive,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Budget-vs-actual for the budget's current cycle window, plus a cycle-end forecast (§12)
/// and the transactions that make up the spend (for the detail view).</summary>
public sealed record BudgetPeriodStatus(
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
    IReadOnlyList<BudgetContributionRow> Contributing);

/// <summary>A single expense that counts toward a budget's cycle spend, for the detail list.</summary>
public sealed record BudgetContributionRow(Guid Id, DateOnly? BookingDate, string? Counterparty, decimal Amount, string Currency, string? Category);

/// <summary>Space-level budget usage signal for the post-sync threshold notifications.</summary>
public sealed record BudgetSignal(Guid BudgetId, string Name, decimal PercentUsed, DateOnly PeriodStart);

public enum BudgetAccessLevel
{
    None,
    Read,
    Write
}

public enum BudgetMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record BudgetMutationOutcome(BudgetMutationResult Result, BudgetView? Budget = null, string? Error = null);

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
            var period = BudgetCycleCalculator.CurrentPeriod(ResolveCycle(budget), asOf);
            var query = db.Transactions.AsNoTracking().Where(transaction =>
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.Amount < 0 &&
                transaction.BookingDate != null &&
                transaction.BookingDate >= period.Start &&
                transaction.BookingDate <= period.End &&
                db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId));
            if (budget.CategoryId.HasValue)
                query = query.Where(transaction => transaction.CategoryId == budget.CategoryId.Value);

            var spent = -(await query.SumAsync(transaction => (decimal?)transaction.Amount, ct) ?? 0m);
            var percentUsed = budget.Amount == 0m
                ? 0m
                : Math.Round(spent / budget.Amount * 100m, 2, MidpointRounding.AwayFromZero);
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

        var visibleAccountIds = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
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

        var spent = -(await ExpensesIn(period).SumAsync(transaction => (decimal?)transaction.Amount, ct) ?? 0m);
        var remaining = budget.Amount - spent;
        var percentUsed = budget.Amount == 0m
            ? 0m
            : Math.Round(spent / budget.Amount * 100m, 2, MidpointRounding.AwayFromZero);

        var previous = BudgetCycleCalculator.PreviousPeriod(cycle, asOf);
        var previousSpent = -(await ExpensesIn(previous).SumAsync(transaction => (decimal?)transaction.Amount, ct) ?? 0m);
        var previousDays = previous.End.DayNumber - previous.Start.DayNumber + 1;
        decimal? historicalDaily = previousSpent > 0m && previousDays > 0 ? previousSpent / previousDays : null;
        var totalDays = period.End.DayNumber - period.Start.DayNumber + 1;
        var elapsedDays = Math.Clamp(asOf.DayNumber - period.Start.DayNumber + 1, 0, totalDays);
        var forecast = Forecast.BudgetForecastCalculator.Project(
            new Forecast.BudgetForecastInput(budget.Amount, spent, totalDays, elapsedDays, historicalDaily));

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
            period.Start, period.End, budget.Amount, spent, remaining, percentUsed,
            forecast.ProjectedEndSpend, forecast.ProjectedOverUnder, forecast.Trend.ToString(), partialAccess, contributing);
    }

    private static BudgetCycleDefinition ResolveCycle(Budget budget) =>
        BudgetCycleResolver.Resolve(budget.Period, budget.StartDate, budget.EndDate);

    public async Task<BudgetAccessLevel> GetAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid budgetId, CancellationToken ct)
    {
        var visible = await VisibleBudgets(userId, fullWorthSpaceId).AnyAsync(budget => budget.Id == budgetId, ct);
        if (!visible) return BudgetAccessLevel.None;
        return await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "budgets.manage", ct)
            ? BudgetAccessLevel.Write
            : BudgetAccessLevel.Read;
    }

    public async Task<BudgetMutationOutcome> CreateForUserAsync(Guid userId, Guid fullWorthSpaceId, BudgetWrite request, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(BudgetMutationResult.NotFound);
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "budgets.manage", ct))
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
            budget.UpdatedAt));

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
        entity.IsActive = request.IsActive;
        entity.StartDate = request.StartDate;
        entity.EndDate = request.EndDate;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public sealed record BudgetWrite(string Name, Guid? CategoryId, decimal Amount, string Currency, string Period, bool CarryOver, bool IsActive, DateOnly? StartDate, DateOnly? EndDate);

public static class BudgetEndpoints
{
    public static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/budgets").WithTags("Budgets");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
            Results.Ok(await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)));

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
        {
            var budget = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return budget is null ? Results.NotFound() : Results.Ok(budget);
        });

        group.MapGet("/{id:guid}/status", async (Guid id, Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
        {
            var asOfDate = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var status = await store.GetStatusForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, asOfDate, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, BudgetWrite request, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, BudgetWrite request, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
            ToResult(await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, BudgetStore store, CancellationToken ct) =>
            ToResult(await store.ArchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static IResult ToResult(BudgetMutationOutcome outcome) => outcome.Result switch
    {
        BudgetMutationResult.Success => Results.Ok(outcome.Budget),
        BudgetMutationResult.NotFound => Results.NotFound(),
        BudgetMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        BudgetMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid budget." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(BudgetMutationResult result) => result switch
    {
        BudgetMutationResult.Success => Results.NoContent(),
        BudgetMutationResult.NotFound => Results.NotFound(),
        BudgetMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        BudgetMutationResult.Invalid => Results.BadRequest(),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
