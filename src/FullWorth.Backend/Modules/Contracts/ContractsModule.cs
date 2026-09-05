using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Parity;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Contracts;

public sealed class RecurringContract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public string Kind { get; set; } = "contract";
    public Guid? CategoryId { get; set; }
    public Guid? AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string BillingCycle { get; set; } = "monthly";
    public int Interval { get; set; } = 1;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? NextDueDate { get; set; }
    public bool AutoDetected { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ContractView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    string? ProviderName,
    string Kind,
    Guid? CategoryId,
    Guid? AccountId,
    decimal Amount,
    string Currency,
    string BillingCycle,
    int Interval,
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateOnly? NextDueDate,
    bool AutoDetected,
    bool IsActive,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // Server-side normalized cost so every client agrees (§7): the recurring amount expressed per month
    // and per year for the contract's billing cycle × interval.
    decimal MonthlyEquivalent = 0m,
    decimal AnnualizedAmount = 0m);

// Optional server-side list filtering/sorting for the contracts view.
public sealed record ContractListQuery(
    string? Kind = null,
    Guid? AccountId = null,
    Guid? CategoryId = null,
    bool? Active = null,
    string? BillingCycle = null,
    string? Sort = null,
    string? Order = null);

public enum ContractAccessLevel
{
    None,
    Read,
    Write
}

public enum ContractMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record ContractMutationOutcome(ContractMutationResult Result, ContractView? Contract = null, string? Error = null);

public sealed class ContractStore(FullWorthDbContext db, AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    public Task<List<RecurringContract>> ListAsync(CancellationToken ct) => ListForSpaceAsync(FullWorthSpaceDefaults.LegacyId, ct);

    public Task<List<RecurringContract>> ListForSpaceAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Contracts.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId).OrderBy(x => x.NextDueDate).ThenBy(x => x.Name).ToListAsync(ct);

    public Task<RecurringContract> UpsertAsync(Guid? id, ContractWrite request, CancellationToken ct) =>
        UpsertForSpaceAsync(FullWorthSpaceDefaults.LegacyId, id, request, ct);

    public async Task<RecurringContract> UpsertForSpaceAsync(Guid fullWorthSpaceId, Guid? id, ContractWrite request, CancellationToken ct)
    {
        await ValidateReferencesAsync(fullWorthSpaceId, request, ct);
        var entity = id.HasValue
            ? await db.Contracts.SingleOrDefaultAsync(x => x.Id == id.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct)
            : null;
        if (id.HasValue && entity is null) throw new InvalidOperationException("Contract not found in FullWorth Space.");
        if (entity is null)
        {
            entity = new RecurringContract { FullWorthSpaceId = fullWorthSpaceId };
            db.Contracts.Add(entity);
        }
        ApplyWrite(entity, request);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public Task<List<ContractView>> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        ListForUserAsync(userId, fullWorthSpaceId, null, ct);

    public async Task<List<ContractView>> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, ContractListQuery? filter, CancellationToken ct)
    {
        var query = VisibleContracts(userId, fullWorthSpaceId);

        // Server-side filters (all optional, additive). Cross-space ids naturally match nothing.
        if (!string.IsNullOrWhiteSpace(filter?.Kind))
        {
            var kind = filter.Kind.Trim().ToLowerInvariant();
            query = query.Where(x => x.Kind == kind);
        }
        if (filter?.AccountId is { } accountId) query = query.Where(x => x.AccountId == accountId);
        if (filter?.CategoryId is { } categoryId) query = query.Where(x => x.CategoryId == categoryId);
        if (filter?.Active is { } active) query = query.Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(filter?.BillingCycle))
        {
            var cycle = filter.BillingCycle.Trim().ToLowerInvariant();
            query = query.Where(x => x.BillingCycle == cycle);
        }

        var contracts = await query.ToListAsync(ct);
        var views = contracts.Select(ToView);
        return Sort(views, filter).ToList();
    }

    public async Task<ContractView?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct)
    {
        var contract = await VisibleContracts(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == contractId, ct);
        return contract is null ? null : ToView(contract);
    }

    // Deterministic monthly/annual math shared by all clients (§7) via the ContractCycle cadence helper.
    private static ContractView ToView(RecurringContract contract)
    {
        var annualized = Math.Round(contract.Amount * ContractCycle.PeriodsPerYear(contract.BillingCycle, contract.Interval), 2, MidpointRounding.AwayFromZero);
        var monthly = Math.Round(contract.Amount * ContractCycle.PeriodsPerYear(contract.BillingCycle, contract.Interval) / 12m, 2, MidpointRounding.AwayFromZero);
        return new ContractView(
            contract.Id,
            contract.FullWorthSpaceId,
            contract.Name,
            contract.ProviderName,
            contract.Kind,
            contract.CategoryId,
            contract.AccountId,
            contract.Amount,
            contract.Currency,
            contract.BillingCycle,
            contract.Interval,
            contract.StartDate,
            contract.EndDate,
            contract.NextDueDate,
            contract.AutoDetected,
            contract.IsActive,
            contract.Notes,
            contract.CreatedAt,
            contract.UpdatedAt,
            monthly,
            annualized);
    }

    private static IEnumerable<ContractView> Sort(IEnumerable<ContractView> views, ContractListQuery? filter)
    {
        var descending = string.Equals(filter?.Order, "desc", StringComparison.OrdinalIgnoreCase);
        Func<ContractView, IComparable?> key = (filter?.Sort?.Trim().ToLowerInvariant()) switch
        {
            "monthly" => v => v.MonthlyEquivalent,
            "annual" or "annualized" => v => v.AnnualizedAmount,
            "account" => v => v.AccountId,
            "category" => v => v.CategoryId,
            "name" => v => v.Name,
            _ => v => v.NextDueDate ?? DateOnly.MaxValue, // default: next-due, nulls last
        };
        var ordered = descending ? views.OrderByDescending(key) : views.OrderBy(key);
        return ordered.ThenBy(v => v.Name);
    }

    public async Task<ContractAccessLevel> GetAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct)
    {
        var contract = await VisibleContracts(userId, fullWorthSpaceId)
            .Where(x => x.Id == contractId)
            .Select(x => new { x.Id, x.AccountId })
            .SingleOrDefaultAsync(ct);
        if (contract is null) return ContractAccessLevel.None;
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "contracts.manage", ct))
            return ContractAccessLevel.Read;
        if (!contract.AccountId.HasValue) return ContractAccessLevel.Write;
        var canWriteAccount = await db.AccountOwners.AsNoTracking().AnyAsync(owner =>
            owner.AccountId == contract.AccountId.Value && owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner, ct);
        return canWriteAccount ? ContractAccessLevel.Write : ContractAccessLevel.Read;
    }

    public async Task<ContractMutationOutcome> CreateForUserAsync(Guid userId, Guid fullWorthSpaceId, ContractWrite request, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(ContractMutationResult.NotFound);
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "contracts.manage", ct))
            return new(ContractMutationResult.Forbidden);

        var referenceResult = await ValidateUserReferencesAsync(userId, fullWorthSpaceId, request, ct);
        if (referenceResult != ContractMutationResult.Success) return new(referenceResult);

        var validationError = ValidateWrite(request);
        if (validationError is not null) return new(ContractMutationResult.Invalid, Error: validationError);

        var entity = new RecurringContract { FullWorthSpaceId = fullWorthSpaceId };
        ApplyWrite(entity, request);
        db.Contracts.Add(entity);
        audit.Record(fullWorthSpaceId, userId, "contract.created", "RecurringContract", entity.Id);
        await db.SaveChangesAsync(ct);
        return new(ContractMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, entity.Id, ct));
    }

    public async Task<ContractMutationOutcome> UpdateForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, ContractWrite request, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, contractId, ct);
        if (access == ContractAccessLevel.None) return new(ContractMutationResult.NotFound);
        if (access != ContractAccessLevel.Write) return new(ContractMutationResult.Forbidden);

        var referenceResult = await ValidateUserReferencesAsync(userId, fullWorthSpaceId, request, ct);
        if (referenceResult != ContractMutationResult.Success) return new(referenceResult);

        var validationError = ValidateWrite(request);
        if (validationError is not null) return new(ContractMutationResult.Invalid, Error: validationError);

        var entity = await db.Contracts.SingleOrDefaultAsync(x => x.Id == contractId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (entity is null) return new(ContractMutationResult.NotFound);
        ApplyWrite(entity, request);
        audit.Record(fullWorthSpaceId, userId, "contract.updated", "RecurringContract", entity.Id);
        await db.SaveChangesAsync(ct);
        return new(ContractMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, contractId, ct));
    }

    public async Task<ContractMutationResult> ArchiveForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct)
    {
        var access = await GetAccessAsync(userId, fullWorthSpaceId, contractId, ct);
        if (access == ContractAccessLevel.None) return ContractMutationResult.NotFound;
        if (access != ContractAccessLevel.Write) return ContractMutationResult.Forbidden;

        var entity = await db.Contracts.SingleOrDefaultAsync(x => x.Id == contractId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (entity is null) return ContractMutationResult.NotFound;
        entity.IsActive = false;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "contract.archived", "RecurringContract", entity.Id);
        await db.SaveChangesAsync(ct);
        return ContractMutationResult.Success;
    }

    // Contract detail activity (UI_UX_SPEC §13): linked payments, payment trend, next expected payment
    // and annualized cost. The date stepping and annualization are owned here (not the browser, §30) via
    // the shared ContractCycle helper so detection and the detail view step cadence identically.
    public async Task<ContractActivity?> GetActivityForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct)
    {
        var contract = await VisibleContracts(userId, fullWorthSpaceId).Where(x => x.Id == contractId).SingleOrDefaultAsync(ct);
        if (contract is null) return null;

        var name = contract.Name.ToLower();
        var provider = string.IsNullOrWhiteSpace(contract.ProviderName) ? null : contract.ProviderName.Trim().ToLower();
        var currency = contract.Currency;
        var accountId = contract.AccountId;

        // Heuristic link: same-currency outflows whose (normalized) counterparty matches the contract
        // name or provider. Contracts do not (yet) store a hard match key, so this stays a best-effort view.
        // When the contract is bound to a specific account, only that account's charges count; otherwise
        // any account visible to the user in the space is considered.
        var visibleAccountIds = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var payments = await db.Transactions.AsNoTracking()
            .Where(t => visibleAccountIds.Contains(t.AccountId))
            .Where(t => accountId == null || t.AccountId == accountId)
            .Where(t => t.Amount < 0 && !t.IsIgnored && !t.IsTransfer && t.Currency == currency)
            .Where(t =>
                (t.NormalizedCounterparty != null && t.NormalizedCounterparty.ToLower() == name)
                || (provider != null && t.Counterparty != null && t.Counterparty.ToLower().Contains(provider))
                || (t.Counterparty != null && t.Counterparty.ToLower().Contains(name)))
            .OrderByDescending(t => t.BookingDate ?? t.ValueDate)
            .Take(60)
            .Select(t => new ContractPayment(t.Id, t.BookingDate ?? t.ValueDate, -t.Amount, t.Currency))
            .ToListAsync(ct);

        var last = payments.Count > 0 ? payments.Max(p => p.Date) : null;
        var average = payments.Count > 0 ? Math.Round(payments.Average(p => p.Amount), 2) : (decimal?)null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var next = ComputeNextExpected(contract, last, today);
        var annualized = Math.Round(contract.Amount * ContractCycle.PeriodsPerYear(contract.BillingCycle, contract.Interval), 2);
        var valueMode = contract.AutoDetected ? "automatic" : "manual";

        return new ContractActivity(contract.Id, valueMode, contract.Amount, currency, annualized, next, last, payments.Count, average, payments);
    }

    private static DateOnly? ComputeNextExpected(RecurringContract contract, DateOnly? lastPayment, DateOnly today)
    {
        if (contract.NextDueDate is { } due && due >= today) return WithinEnd(contract, due);
        var cursor = contract.NextDueDate ?? lastPayment;
        if (cursor is null) return null;
        return WithinEnd(contract, ContractCycle.NextOnOrAfter(cursor.Value, contract.BillingCycle, contract.Interval, today));
    }

    private static DateOnly? WithinEnd(RecurringContract contract, DateOnly candidate) =>
        contract.EndDate is { } end && candidate > end ? null : candidate;

    private IQueryable<RecurringContract> VisibleContracts(Guid userId, Guid fullWorthSpaceId) =>
        db.Contracts.AsNoTracking().Where(contract =>
            contract.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            (contract.AccountId == null || db.AccountOwners.Any(owner => owner.AccountId == contract.AccountId.Value && owner.UserId == userId)));

    private Task<string?> GetSpaceRoleAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);

    private async Task<ContractMutationResult> ValidateUserReferencesAsync(Guid userId, Guid fullWorthSpaceId, ContractWrite request, CancellationToken ct)
    {
        if (request.CategoryId.HasValue &&
            !await db.Categories.AsNoTracking().AnyAsync(category => category.Id == request.CategoryId.Value && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return ContractMutationResult.NotFound;

        if (!request.AccountId.HasValue) return ContractMutationResult.Success;

        var ownership = await db.Accounts.AsNoTracking()
            .Where(account => account.Id == request.AccountId.Value && account.FullWorthSpaceId == fullWorthSpaceId)
            .Join(db.AccountOwners.AsNoTracking().Where(owner => owner.UserId == userId),
                account => account.Id,
                owner => owner.AccountId,
                (account, owner) => owner.OwnershipType)
            .SingleOrDefaultAsync(ct);

        return ownership switch
        {
            AccountOwnershipTypes.Owner => ContractMutationResult.Success,
            AccountOwnershipTypes.Viewer => ContractMutationResult.Forbidden,
            _ => ContractMutationResult.NotFound
        };
    }

    private async Task ValidateReferencesAsync(Guid fullWorthSpaceId, ContractWrite request, CancellationToken ct)
    {
        if (request.CategoryId.HasValue &&
            !await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct))
            throw new InvalidOperationException("Contract category must belong to the same FullWorth Space.");

        if (request.AccountId.HasValue &&
            !await db.Accounts.AsNoTracking().AnyAsync(x => x.Id == request.AccountId.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct))
            throw new InvalidOperationException("Contract account must belong to the same FullWorth Space.");
    }

    private static string? ValidateWrite(ContractWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Contract name is required.";
        if (string.IsNullOrWhiteSpace(request.Currency)) return "Currency is required.";
        var currency = request.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')) return "Currency must be a three-letter code.";
        if (string.IsNullOrWhiteSpace(request.BillingCycle)) return "Billing cycle is required.";
        return null;
    }

    private static void ApplyWrite(RecurringContract entity, ContractWrite request)
    {
        entity.Name = request.Name.Trim();
        entity.ProviderName = request.ProviderName?.Trim();
        entity.Kind = string.IsNullOrWhiteSpace(request.Kind) ? "contract" : request.Kind.Trim().ToLowerInvariant();
        entity.CategoryId = request.CategoryId;
        entity.AccountId = request.AccountId;
        entity.Amount = request.Amount;
        entity.Currency = request.Currency.Trim().ToUpperInvariant();
        entity.BillingCycle = request.BillingCycle.Trim().ToLowerInvariant();
        entity.Interval = Math.Max(1, request.Interval);
        entity.StartDate = request.StartDate;
        entity.EndDate = request.EndDate;
        entity.NextDueDate = request.NextDueDate;
        entity.IsActive = request.IsActive;
        entity.Notes = request.Notes?.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public sealed record ContractWrite(string Name, string? ProviderName, string Kind, Guid? CategoryId, Guid? AccountId, decimal Amount, string Currency, string BillingCycle, int Interval, DateOnly? StartDate, DateOnly? EndDate, DateOnly? NextDueDate, bool IsActive, string? Notes);

public sealed record ContractPayment(Guid Id, DateOnly? Date, decimal Amount, string Currency);
public sealed record ContractActivity(
    Guid ContractId,
    string ValueMode,
    decimal ExpectedAmount,
    string Currency,
    decimal AnnualizedAmount,
    DateOnly? NextExpected,
    DateOnly? LastPayment,
    int MatchedCount,
    decimal? AverageAmount,
    IReadOnlyList<ContractPayment> Payments);

// Cadence math shared by contract detection and the detail view so both step identically.
public static class ContractCycle
{
    public static DateOnly Next(DateOnly date, string? cycle, int interval)
    {
        interval = Math.Max(1, interval);
        return (cycle ?? "monthly").Trim().ToLowerInvariant() switch
        {
            "weekly" => date.AddDays(7 * interval),
            "quarterly" => date.AddMonths(3 * interval),
            "yearly" => date.AddYears(interval),
            "daily" => date.AddDays(interval),
            _ => date.AddMonths(interval)
        };
    }

    public static decimal PeriodsPerYear(string? cycle, int interval)
    {
        interval = Math.Max(1, interval);
        return (cycle ?? "monthly").Trim().ToLowerInvariant() switch
        {
            "weekly" => 52m / interval,
            "quarterly" => 4m / interval,
            "yearly" => 1m / interval,
            "daily" => 365m / interval,
            _ => 12m / interval
        };
    }

    public static DateOnly NextOnOrAfter(DateOnly start, string? cycle, int interval, DateOnly onOrAfter)
    {
        if (start >= onOrAfter) return start;
        var c = (cycle ?? "monthly").Trim().ToLowerInvariant();
        interval = Math.Max(1, interval);
        if (c is "daily" or "weekly")
        {
            var stepDays = (c == "weekly" ? 7 : 1) * interval;
            var wholePeriodsBehind = (onOrAfter.DayNumber - start.DayNumber) / stepDays;
            var next = start.AddDays(wholePeriodsBehind * stepDays);
            if (next < onOrAfter) next = next.AddDays(stepDays);
            return next;
        }
        var guard = 0;
        var cursor = start;
        while (cursor < onOrAfter && guard++ < 2400) cursor = Next(cursor, c, interval);
        return cursor;
    }
}

public static class ContractEndpoints
{
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts").WithTags("Contracts");

        group.MapGet("/", async (Guid fullWorthSpaceId, string? kind, Guid? accountId, Guid? categoryId, bool? active, string? billingCycle, string? sort, string? order, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            Results.Ok(await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId,
                new ContractListQuery(kind, accountId, categoryId, active, billingCycle, sort, order), ct)));

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
        {
            var contract = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return contract is null ? Results.NotFound() : Results.Ok(contract);
        });

        group.MapGet("/{id:guid}/activity", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
        {
            var activity = await store.GetActivityForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return activity is null ? Results.NotFound() : Results.Ok(activity);
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, ContractWrite request, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, ContractWrite request, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            ToResult(await store.ArchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        return app;
    }

    private static IResult ToResult(ContractMutationOutcome outcome) => outcome.Result switch
    {
        ContractMutationResult.Success => Results.Ok(outcome.Contract),
        ContractMutationResult.NotFound => Results.NotFound(),
        ContractMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        ContractMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid contract." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(ContractMutationResult result) => result switch
    {
        ContractMutationResult.Success => Results.NoContent(),
        ContractMutationResult.NotFound => Results.NotFound(),
        ContractMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        ContractMutationResult.Invalid => Results.BadRequest(),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
