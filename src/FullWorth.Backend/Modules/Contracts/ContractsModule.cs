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
    public Guid? MergedIntoContractId { get; set; }
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
    decimal MonthlyEquivalent = 0,
    decimal AnnualizedAmount = 0);

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

public sealed record ContractMergeRequest(IReadOnlyList<Guid> SourceContractIds);

public sealed record ContractMergeSourceView(
    Guid Id,
    string Name,
    string? ProviderName,
    Guid? AccountId,
    decimal Amount,
    string Currency,
    string BillingCycle,
    DateOnly? NextDueDate);

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

    public async Task<List<ContractView>> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var contracts = await VisibleContracts(userId, fullWorthSpaceId)
            .OrderBy(x => x.NextDueDate)
            .ThenBy(x => x.Name)
            .ToListAsync(ct);
        return contracts.Select(ToView).ToList();
    }

    public async Task<ContractView?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct)
    {
        var contract = await VisibleContracts(userId, fullWorthSpaceId)
            .SingleOrDefaultAsync(x => x.Id == contractId, ct);
        return contract is null ? null : ToView(contract);
    }

    public async Task<ContractAccessLevel> GetAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct)
    {
        var contract = await VisibleContractRecords(userId, fullWorthSpaceId)
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

    public async Task<IReadOnlyList<ContractMergeSourceView>?> ListMergedSourcesForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid contractId,
        CancellationToken ct)
    {
        if (!await VisibleContracts(userId, fullWorthSpaceId).AnyAsync(contract => contract.Id == contractId, ct))
            return null;

        return await VisibleContractRecords(userId, fullWorthSpaceId)
            .Where(contract => contract.MergedIntoContractId == contractId)
            .OrderBy(contract => contract.CreatedAt)
            .Select(contract => new ContractMergeSourceView(
                contract.Id,
                contract.Name,
                contract.ProviderName,
                contract.AccountId,
                contract.Amount,
                contract.Currency,
                contract.BillingCycle,
                contract.NextDueDate))
            .ToListAsync(ct);
    }

    public async Task<ContractMutationOutcome> MergeForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid targetContractId,
        ContractMergeRequest request,
        CancellationToken ct)
    {
        var targetAccess = await GetAccessAsync(userId, fullWorthSpaceId, targetContractId, ct);
        if (targetAccess == ContractAccessLevel.None) return new(ContractMutationResult.NotFound);
        if (targetAccess != ContractAccessLevel.Write) return new(ContractMutationResult.Forbidden);

        var target = await db.Contracts.SingleOrDefaultAsync(contract =>
            contract.Id == targetContractId &&
            contract.FullWorthSpaceId == fullWorthSpaceId &&
            contract.MergedIntoContractId == null, ct);
        if (target is null) return new(ContractMutationResult.NotFound);

        var sourceIds = (request.SourceContractIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty && id != targetContractId)
            .Distinct()
            .ToArray();
        if (sourceIds.Length == 0)
            return new(ContractMutationResult.Invalid, Error: "Select at least one contract to merge.");

        var sources = await db.Contracts
            .Where(contract =>
                contract.FullWorthSpaceId == fullWorthSpaceId &&
                sourceIds.Contains(contract.Id) &&
                contract.MergedIntoContractId == null)
            .ToListAsync(ct);
        if (sources.Count != sourceIds.Length) return new(ContractMutationResult.NotFound);
        if (sources.Any(source => !string.Equals(source.Currency, target.Currency, StringComparison.OrdinalIgnoreCase)))
            return new(ContractMutationResult.Invalid, Error: "Contracts with different currencies cannot be merged.");

        foreach (var source in sources)
        {
            var access = await GetAccessAsync(userId, fullWorthSpaceId, source.Id, ct);
            if (access == ContractAccessLevel.None) return new(ContractMutationResult.NotFound);
            if (access != ContractAccessLevel.Write) return new(ContractMutationResult.Forbidden);
        }

        // Keep the merge graph flat. If a selected source already owns older aliases/account-history,
        // re-parent those records to the selected target before hiding the source row itself.
        var nestedSources = await db.Contracts
            .Where(contract =>
                contract.FullWorthSpaceId == fullWorthSpaceId &&
                contract.MergedIntoContractId.HasValue &&
                sourceIds.Contains(contract.MergedIntoContractId.Value))
            .ToListAsync(ct);
        foreach (var nested in nestedSources)
        {
            nested.MergedIntoContractId = target.Id;
            nested.UpdatedAt = DateTimeOffset.UtcNow;
        }

        foreach (var source in sources)
        {
            source.MergedIntoContractId = target.Id;
            source.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Record(fullWorthSpaceId, userId, "contract.merged", "RecurringContract", source.Id);
        }

        target.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(ContractMutationResult.Success, await GetForUserAsync(userId, fullWorthSpaceId, target.Id, ct));
    }

    public async Task<ContractMutationResult> UnmergeForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid targetContractId,
        Guid sourceContractId,
        CancellationToken ct)
    {
        var targetAccess = await GetAccessAsync(userId, fullWorthSpaceId, targetContractId, ct);
        if (targetAccess == ContractAccessLevel.None) return ContractMutationResult.NotFound;
        if (targetAccess != ContractAccessLevel.Write) return ContractMutationResult.Forbidden;

        var source = await db.Contracts.SingleOrDefaultAsync(contract =>
            contract.Id == sourceContractId &&
            contract.FullWorthSpaceId == fullWorthSpaceId &&
            contract.MergedIntoContractId == targetContractId, ct);
        if (source is null) return ContractMutationResult.NotFound;

        var sourceAccess = await GetAccessAsync(userId, fullWorthSpaceId, sourceContractId, ct);
        if (sourceAccess == ContractAccessLevel.None) return ContractMutationResult.NotFound;
        if (sourceAccess != ContractAccessLevel.Write) return ContractMutationResult.Forbidden;

        source.MergedIntoContractId = null;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "contract.unmerged", "RecurringContract", source.Id);
        await db.SaveChangesAsync(ct);
        return ContractMutationResult.Success;
    }

    // Contract detail activity (UI_UX_SPEC §13): linked payments, payment trend, next expected payment
    // and annualized cost. The date stepping and annualization are owned here (not the browser, §30) via
    // the shared ContractCycle helper so detection and the detail view step cadence identically.
    public async Task<ContractActivity?> GetActivityForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct)
    {
        var contract = await VisibleContracts(userId, fullWorthSpaceId)
            .Where(x => x.Id == contractId)
            .SingleOrDefaultAsync(ct);
        if (contract is null) return null;

        var mergedSources = await VisibleContractRecords(userId, fullWorthSpaceId)
            .Where(source => source.MergedIntoContractId == contractId)
            .ToListAsync(ct);
        var identities = new[] { contract }.Concat(mergedSources).ToArray();

        var aliases = identities
            .SelectMany(item => new[] { item.Name, item.ProviderName })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ContractIdentity.Normalize(value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var currency = contract.Currency;
        var linkedAccountIds = identities
            .Where(item => item.AccountId.HasValue)
            .Select(item => item.AccountId!.Value)
            .Distinct()
            .ToArray();
        var hasUnboundSource = identities.Any(item => !item.AccountId.HasValue);

        var visibleAccountIds = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var transactionQuery = db.Transactions.AsNoTracking()
            .Where(transaction => visibleAccountIds.Contains(transaction.AccountId))
            .Where(transaction =>
                transaction.Amount < 0 &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.Currency == currency);

        if (!hasUnboundSource && linkedAccountIds.Length > 0)
            transactionQuery = transactionQuery.Where(transaction => linkedAccountIds.Contains(transaction.AccountId));

        // Load a bounded recent set, then apply the shared identity normalizer in memory. This lets one
        // logical contract match all merged aliases (including account-change spelling variants such as
        // Ö/OE and ß/SS) without maintaining browser-side matching rules.
        var candidates = await transactionQuery
            .OrderByDescending(transaction => transaction.BookingDate ?? transaction.ValueDate)
            .Take(400)
            .Select(transaction => new
            {
                transaction.Id,
                Date = transaction.BookingDate ?? transaction.ValueDate,
                Amount = -transaction.Amount,
                transaction.Currency,
                transaction.Counterparty,
                transaction.NormalizedCounterparty
            })
            .ToListAsync(ct);

        var payments = candidates
            .Where(transaction => aliases.Any(alias =>
                ContractIdentity.Matches(alias, transaction.NormalizedCounterparty) ||
                ContractIdentity.Matches(alias, transaction.Counterparty)))
            .Take(60)
            .Select(transaction => new ContractPayment(transaction.Id, transaction.Date, transaction.Amount, transaction.Currency))
            .ToList();

        var last = payments.Count > 0 ? payments.Max(payment => payment.Date) : null;
        var average = payments.Count > 0 ? Math.Round(payments.Average(payment => payment.Amount), 2) : (decimal?)null;
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
        VisibleContractRecords(userId, fullWorthSpaceId)
            .Where(contract => contract.MergedIntoContractId == null);

    private IQueryable<RecurringContract> VisibleContractRecords(Guid userId, Guid fullWorthSpaceId) =>
        db.Contracts.AsNoTracking().Where(contract =>
            contract.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            (contract.AccountId == null || db.AccountOwners.Any(owner => owner.AccountId == contract.AccountId.Value && owner.UserId == userId)));

    private static ContractView ToView(RecurringContract contract)
    {
        // One cadence calculation path for list/detail/detection. Activity already uses
        // ContractCycle.PeriodsPerYear; list normalization now uses the same source of truth.
        var annualized = Math.Round(
            contract.Amount * ContractCycle.PeriodsPerYear(contract.BillingCycle, contract.Interval),
            2,
            MidpointRounding.AwayFromZero);
        var monthly = Math.Round(annualized / 12m, 2, MidpointRounding.AwayFromZero);

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

public static class ContractIdentity
{
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AG", "GMBH", "KG", "OHG", "SE", "SA", "SAS", "BV", "NV", "INC", "LTD", "LLC", "PLC", "AB"
    };

    public static string Normalize(string? value)
    {
        var folded = (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Replace("Ä", "AE", StringComparison.Ordinal)
            .Replace("Ö", "OE", StringComparison.Ordinal)
            .Replace("Ü", "UE", StringComparison.Ordinal)
            .Replace("ẞ", "SS", StringComparison.Ordinal)
            .Replace("ß", "SS", StringComparison.Ordinal);

        var normalized = new string(folded
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray());
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && LegalSuffixes.Contains(tokens[^1])) tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens);
    }

    public static bool Matches(string normalizedIdentity, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentity) || string.IsNullOrWhiteSpace(candidate)) return false;
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate.Length == 0) return false;
        if (string.Equals(normalizedIdentity, normalizedCandidate, StringComparison.Ordinal)) return true;

        var left = normalizedIdentity.Replace(" ", string.Empty, StringComparison.Ordinal);
        var right = normalizedCandidate.Replace(" ", string.Empty, StringComparison.Ordinal);
        return left.Length >= 6 && right.Length >= 6 &&
               (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));
    }
}

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

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
            Results.Ok(await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)));

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

        group.MapGet("/{id:guid}/merged-sources", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, ContractStore store, CancellationToken ct) =>
        {
            var sources = await store.ListMergedSourcesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return sources is null ? Results.NotFound() : Results.Ok(sources);
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
