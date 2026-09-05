using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

public sealed class FinanceAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    // Null for manual accounts (e.g. cash) that exist without any bank connection.
    public Guid? BankConnectionId { get; set; }
    public string Provider { get; set; } = "enable-banking";
    public string IdentificationHash { get; set; } = string.Empty;
    // Enable Banking may expose several equivalent hashes (e.g. IBAN/BBAN and legacy hash versions).
    // Keep the aliases so a later session can resolve the same account even when the primary hash changes.
    public string IdentificationHashesJson { get; set; } = "[]";
    public string ProviderAccountId { get; set; } = string.Empty;
    public string InstitutionName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Product { get; set; }
    public string? AccountType { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? IbanLast4 { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IncludeInNetWorth { get; set; } = true;
    public int SortOrder { get; set; }
    // Optional user-defined group (§8.1). SetNull on group delete, so accounts are never orphaned.
    public Guid? GroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<AccountOwner> Owners { get; set; } = [];
}

/// <summary>A user-defined, reorderable group of accounts within a space (UI_UX_SPEC §8.1).</summary>
public sealed class AccountGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BalanceSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string BalanceType { get; set; } = string.Empty;
    public DateOnly? ReferenceDate { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record BalanceView(decimal Amount, string Currency, string BalanceType, DateTimeOffset CapturedAt);

public static class BalanceSnapshotQueries
{
    // Deterministic "current balance" selection. A sync stamps EVERY provider balance_type
    // (interimAvailable, closingBooked, …) with the same CapturedAt, so ordering by CapturedAt alone
    // let the chosen balance — and thus the displayed amount and net worth — flip arbitrarily between
    // available/booked from one sync to the next. Newest capture first, then a single sort key that
    // encodes the balance-type preference (a one-digit rank prefix) followed by the type name as an
    // in-bucket tiebreak → always the same balance for the same data.
    //
    // Why one concatenated key instead of several ThenBy() keys: a longer key chain (or an
    // integer-valued CASE) fails to translate. A single string key with a CASE rank prefix translates
    // and preserves both the preference and a deterministic alphabetical fallback.
    //
    // IMPORTANT: this extension only works as a TOP-LEVEL query (e.g. NetWorthSnapshotService), where
    // its body is invoked and composes into the query. Inside a correlated FirstOrDefault subquery that
    // lives in a projection lambda (accounts list, analytics, export) EF cannot expand a user method —
    // extension OR helper — and throws "could not be translated" at runtime (500). Those sites must
    // write the SAME conditional key inline; the CASE below is duplicated there by necessity. Keep in sync.
    public static IOrderedQueryable<BalanceSnapshot> CurrentFirst(this IQueryable<BalanceSnapshot> source) =>
        source.OrderByDescending(b => b.CapturedAt)
              .ThenBy(b => (b.BalanceType == "interimAvailable" ? "0"
                          : b.BalanceType == "closingAvailable" ? "1"
                          : b.BalanceType == "closingBooked" ? "2"
                          : b.BalanceType == "interimBooked" ? "3"
                          : b.BalanceType == "expected" ? "4" : "5") + b.BalanceType);
}
// BaseValue/BaseCurrency (§18): the latest balance converted into the space's base currency, for the
// "native first, smaller converted base underneath" row display. Null when the account is already in
// the base currency or no conversion rate is available (the row then shows only its native amount).
public sealed record AccountListItem(Guid Id, Guid FullWorthSpaceId, Guid? BankConnectionId, string InstitutionName, string DisplayName, string? Product, string? AccountType, string Currency, string? IbanLast4, bool IsActive, bool IncludeInNetWorth, int SortOrder, DateTimeOffset UpdatedAt, string Provider, BalanceView? LatestBalance, Guid? GroupId = null, string? GroupName = null, decimal? BaseValue = null, string? BaseCurrency = null);
public sealed record AccountCreateRequest(Guid FullWorthSpaceId, Guid? BankConnectionId, string DisplayName, string? Currency, bool? IncludeInNetWorth, int? SortOrder, string? InstitutionName = null, decimal? InitialBalance = null);
public sealed record AccountSettingsRequest(string? DisplayName, bool? IsActive, bool? IncludeInNetWorth, int? SortOrder);
public sealed record ManualBalanceRequest(decimal Amount, string? Currency);
public sealed record AccountGroupDto(Guid Id, Guid FullWorthSpaceId, string Name, int SortOrder);
public sealed record AccountGroupWrite(string Name, int? SortOrder);
public sealed record AccountGroupAssignRequest(Guid? GroupId);
public enum AccountGroupResult { Ok, NotFound, Forbidden }

public sealed class AccountStore(FullWorthDbContext db, AuditService? auditService = null, FullWorth.Backend.Modules.Fx.CurrencyConverter? fx = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    public async Task<List<AccountListItem>> ListForUserAsync(Guid userId, Guid? fullWorthSpaceId, CancellationToken ct)
    {
        var items = await Project(AccessibleAccounts(userId, fullWorthSpaceId)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.InstitutionName).ThenBy(x => x.DisplayName))
            .ToListAsync(ct);
        items = await WithConvertedBalancesAsync(items, fullWorthSpaceId, ct);
        return WithDisplayIdentifiers(items);
    }

    // §18: fill each foreign account's balance converted into the space base currency for the row's
    // secondary line. Needs an in-memory FX snapshot so it can't live in the EF projection above.
    private async Task<List<AccountListItem>> WithConvertedBalancesAsync(List<AccountListItem> items, Guid? fullWorthSpaceId, CancellationToken ct)
    {
        if (fx is null || fullWorthSpaceId is null || items.Count == 0) return items;
        var baseCurrency = await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId.Value).Select(space => space.BaseCurrency).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(baseCurrency)) return items;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var snapshot = await fx.PrepareLatestAsync(baseCurrency, today, ct);
        return items.Select(item =>
        {
            var balance = item.LatestBalance;
            if (balance is null || string.Equals(balance.Currency, baseCurrency, StringComparison.OrdinalIgnoreCase)) return item;
            var converted = snapshot.ToBaseOn(balance.Amount, balance.Currency, today);
            return converted is null ? item : item with { BaseValue = converted.Value, BaseCurrency = baseCurrency };
        }).ToList();
    }

    // Finanzguru-style account rows always carry a visible identifier. Prefer the bank-provided IBAN
    // suffix. Accounts without one (cash/manual accounts, PayPal-like providers, cards without PAN
    // metadata, etc.) receive a stable app-local #code derived from the account UUID. If two accounts
    // happen to share the same external last four characters, append that #code to both so the list is
    // still unambiguous. The code length automatically grows until it is unique within this result set.
    private static List<AccountListItem> WithDisplayIdentifiers(List<AccountListItem> items)
    {
        if (items.Count == 0) return items;

        var rawCodes = items.ToDictionary(item => item.Id, item => item.Id.ToString("N").ToUpperInvariant());
        var codeLength = 6;
        while (codeLength < 32 && rawCodes.Values
                   .Select(code => code[..codeLength])
                   .Distinct(StringComparer.Ordinal)
                   .Count() != rawCodes.Count)
            codeLength = Math.Min(32, codeLength + 2);

        var duplicateExternalSuffixes = items
            .Where(item => !string.IsNullOrWhiteSpace(item.IbanLast4))
            .GroupBy(item => item.IbanLast4!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return items.Select(item =>
        {
            var code = rawCodes[item.Id][..codeLength];
            if (string.IsNullOrWhiteSpace(item.IbanLast4))
                return item with { IbanLast4 = $"#{code}" };

            var external = item.IbanLast4.Trim();
            return duplicateExternalSuffixes.Contains(external)
                ? item with { IbanLast4 = $"{external} · #{code}" }
                : item with { IbanLast4 = external };
        }).ToList();
    }

    public async Task<AccountListItem?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct)
    {
        var item = await Project(AccessibleAccounts(userId, fullWorthSpaceId).Where(x => x.Id == accountId))
            .SingleOrDefaultAsync(ct);
        return item is null ? null : WithDisplayIdentifiers([item])[0];
    }

    public Task<List<AccountOwnerDto>> ListOwnersAsync(Guid accountId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Set<AccountOwner>().AsNoTracking()
            .Where(x => x.AccountId == accountId && x.Account.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(x => x.UserId)
            .Select(x => new AccountOwnerDto(x.AccountId, x.UserId, x.OwnershipType, x.CreatedAt))
            .ToListAsync(ct);

    public Task<bool> HasAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct) =>
        AccessibleAccounts(userId, fullWorthSpaceId).AnyAsync(x => x.Id == accountId, ct);

    public Task<bool> HasEditAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct) =>
        db.Accounts.AsNoTracking().AnyAsync(x =>
            x.Id == accountId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            x.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner), ct);

    public Task<bool> OwnerExistsAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct) =>
        db.Set<AccountOwner>().AsNoTracking().AnyAsync(x =>
            x.AccountId == accountId &&
            x.UserId == userId &&
            x.Account.FullWorthSpaceId == fullWorthSpaceId, ct);

    public Task<int> CountOwnersAsync(Guid fullWorthSpaceId, Guid accountId, CancellationToken ct) =>
        db.Set<AccountOwner>().AsNoTracking().CountAsync(x =>
            x.AccountId == accountId &&
            x.Account.FullWorthSpaceId == fullWorthSpaceId &&
            x.OwnershipType == AccountOwnershipTypes.Owner, ct);

    public Task<AccountOwner?> GetOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct) =>
        db.Set<AccountOwner>().SingleOrDefaultAsync(x =>
            x.AccountId == accountId &&
            x.UserId == userId &&
            x.Account.FullWorthSpaceId == fullWorthSpaceId, ct);

    public async Task<AccountListItem?> CreateForMemberAsync(Guid userId, AccountCreateRequest request, CancellationToken ct)
    {
        ValidateCreateRequest(request);

        string institutionName;
        if (request.BankConnectionId.HasValue)
        {
            var connection = await db.BankConnections.AsNoTracking().SingleOrDefaultAsync(connection =>
                connection.Id == request.BankConnectionId.Value &&
                connection.FullWorthSpaceId == request.FullWorthSpaceId &&
                db.FullWorthSpaceMembers.Any(member =>
                    member.FullWorthSpaceId == request.FullWorthSpaceId && member.UserId == userId), ct);
            if (connection is null) return null;
            institutionName = connection.InstitutionName;
        }
        else
        {
            // Manual account without any bank connection (e.g. cash): the caller still has to be a
            // member of the target space — same not-found contract as the connection-backed path.
            var isMember = await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
                member.FullWorthSpaceId == request.FullWorthSpaceId && member.UserId == userId, ct);
            if (!isMember) return null;
            institutionName = string.IsNullOrWhiteSpace(request.InstitutionName) ? "Manual" : request.InstitutionName.Trim();
        }

        var now = DateTimeOffset.UtcNow;
        var manualKey = Guid.NewGuid().ToString("N");
        var account = new FinanceAccount
        {
            FullWorthSpaceId = request.FullWorthSpaceId,
            BankConnectionId = request.BankConnectionId,
            Provider = "manual",
            IdentificationHash = $"manual:{manualKey}",
            ProviderAccountId = $"manual:{manualKey}",
            InstitutionName = institutionName,
            DisplayName = request.DisplayName.Trim(),
            Currency = NormalizeCurrency(request.Currency),
            IncludeInNetWorth = request.IncludeInNetWorth ?? true,
            SortOrder = request.SortOrder ?? 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        account.Owners.Add(new AccountOwner
        {
            Account = account,
            UserId = userId,
            OwnershipType = AccountOwnershipTypes.Owner,
            CreatedAt = now
        });

        db.Accounts.Add(account);

        BalanceView? initialBalance = null;
        if (request.InitialBalance.HasValue)
        {
            var snapshot = new BalanceSnapshot
            {
                AccountId = account.Id,
                Amount = request.InitialBalance.Value,
                Currency = account.Currency,
                BalanceType = "manual",
                ReferenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CapturedAt = now
            };
            db.BalanceSnapshots.Add(snapshot);
            initialBalance = new BalanceView(snapshot.Amount, snapshot.Currency, snapshot.BalanceType, snapshot.CapturedAt);
        }

        await db.SaveChangesAsync(ct);

        var created = new AccountListItem(
            account.Id, account.FullWorthSpaceId, account.BankConnectionId, account.InstitutionName, account.DisplayName,
            account.Product, account.AccountType, account.Currency, account.IbanLast4, account.IsActive,
            account.IncludeInNetWorth, account.SortOrder, account.UpdatedAt, account.Provider, initialBalance);
        return WithDisplayIdentifiers([created])[0];
    }

    /// <summary>
    /// Records a new balance snapshot for a manual account. Only account owners may set it, and only
    /// on connection-less provider "manual" accounts — any account tied to a bank connection gets its
    /// balances from that connection. Ordering mirrors PATCH/DELETE: not-found → forbidden → conflict.
    /// </summary>
    public async Task<ManualBalanceResult> SetManualBalanceAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, ManualBalanceRequest request, CancellationToken ct)
    {
        ValidateAmount(request.Amount);

        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == accountId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            x.Owners.Any(owner => owner.UserId == userId), ct);
        if (account is null) return ManualBalanceResult.NotFound;

        var isOwner = await db.Set<AccountOwner>().AsNoTracking().AnyAsync(x =>
            x.AccountId == accountId && x.UserId == userId && x.OwnershipType == AccountOwnershipTypes.Owner, ct);
        if (!isOwner) return ManualBalanceResult.Forbidden;
        if (account.Provider != "manual" || account.BankConnectionId is not null) return ManualBalanceResult.NotManual;

        // A snapshot in a different currency would silently corrupt net worth: the aggregation sums
        // the latest snapshot per account bucketed by the ACCOUNT's currency.
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? account.Currency : NormalizeCurrency(request.Currency);
        if (currency != account.Currency) throw new ArgumentException("Currency must match the account currency.");

        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = accountId,
            Amount = request.Amount,
            Currency = currency,
            BalanceType = "manual",
            ReferenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CapturedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return ManualBalanceResult.Ok;
    }

    // --- Account groups (§8.1). Group CRUD is space-member gated (like account create); assignment is
    // account-owner gated (like the manual-balance write). ---

    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId, ct);

    public async Task<(bool Found, List<AccountGroupDto>? Groups)> ListGroupsForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (false, null);
        var groups = await db.AccountGroups.AsNoTracking()
            .Where(g => g.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
            .Select(g => new AccountGroupDto(g.Id, g.FullWorthSpaceId, g.Name, g.SortOrder))
            .ToListAsync(ct);
        return (true, groups);
    }

    public async Task<AccountGroupDto?> CreateGroupForMemberAsync(Guid userId, Guid fullWorthSpaceId, AccountGroupWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null; // → 404
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("Group name is required.");
        var group = new AccountGroup { FullWorthSpaceId = fullWorthSpaceId, Name = name, SortOrder = request.SortOrder ?? 0 };
        db.AccountGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return new AccountGroupDto(group.Id, group.FullWorthSpaceId, group.Name, group.SortOrder);
    }

    public async Task<bool> RenameGroupForMemberAsync(Guid userId, Guid fullWorthSpaceId, Guid groupId, AccountGroupWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return false;
        var group = await db.AccountGroups.SingleOrDefaultAsync(g => g.Id == groupId && g.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (group is null) return false;
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("Group name is required.");
        group.Name = name;
        if (request.SortOrder.HasValue) group.SortOrder = request.SortOrder.Value;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteGroupForMemberAsync(Guid userId, Guid fullWorthSpaceId, Guid groupId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return false;
        var group = await db.AccountGroups.SingleOrDefaultAsync(g => g.Id == groupId && g.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (group is null) return false;
        db.AccountGroups.Remove(group); // FK SetNull auto-ungroups its accounts
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Owner-gated assignment; not-found → forbidden ordering mirrors SetManualBalanceAsync. A group from
    // another space (or a missing one) is rejected as NotFound to prevent cross-space assignment.
    public async Task<AccountGroupResult> AssignAccountToGroupAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, Guid? groupId, CancellationToken ct)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(x =>
            x.Id == accountId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            x.Owners.Any(owner => owner.UserId == userId), ct);
        if (account is null) return AccountGroupResult.NotFound;

        var isOwner = await db.Set<AccountOwner>().AsNoTracking().AnyAsync(x =>
            x.AccountId == accountId && x.UserId == userId && x.OwnershipType == AccountOwnershipTypes.Owner, ct);
        if (!isOwner) return AccountGroupResult.Forbidden;

        if (groupId.HasValue && !await db.AccountGroups.AsNoTracking().AnyAsync(g => g.Id == groupId.Value && g.FullWorthSpaceId == fullWorthSpaceId, ct))
            return AccountGroupResult.NotFound;

        account.GroupId = groupId;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return AccountGroupResult.Ok;
    }

    public async Task<bool> InsertOwnerAsync(Guid fullWorthSpaceId, Guid accountId, Guid userId, string ownershipType, CancellationToken ct)
        => await InsertOwnerAsync(fullWorthSpaceId, accountId, userId, ownershipType, null, ct);

    public async Task<bool> InsertOwnerAsync(Guid fullWorthSpaceId, Guid accountId, Guid userId, string ownershipType, Guid? actorUserId, CancellationToken ct)
    {
        var accountExists = await db.Accounts.AsNoTracking().AnyAsync(x => x.Id == accountId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (!accountExists) return false;

        db.Set<AccountOwner>().Add(new AccountOwner
        {
            AccountId = accountId,
            UserId = userId,
            OwnershipType = ownershipType,
            CreatedAt = DateTimeOffset.UtcNow
        });
        audit.Record(fullWorthSpaceId, actorUserId, "account.ownership.granted", "AccountOwner", accountId);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteOwnerAsync(AccountOwner owner, CancellationToken ct)
        => await DeleteOwnerAsync(owner, null, null, ct);

    public async Task DeleteOwnerAsync(AccountOwner owner, Guid? actorUserId, Guid? fullWorthSpaceId, CancellationToken ct)
    {
        db.Set<AccountOwner>().Remove(owner);
        if (fullWorthSpaceId.HasValue)
            audit.Record(fullWorthSpaceId, actorUserId, "account.ownership.revoked", "AccountOwner", owner.AccountId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateSettingsForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, AccountSettingsRequest request, CancellationToken ct)
    {
        var entity = await db.Accounts.SingleOrDefaultAsync(x =>
            x.Id == accountId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            x.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner), ct);
        if (entity is null) return false;

        ApplySettings(entity, request);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ArchiveForOwnerAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct)
    {
        var entity = await db.Accounts.SingleOrDefaultAsync(x =>
            x.Id == accountId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            x.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner), ct);
        if (entity is null) return false;

        entity.IsActive = false;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private IQueryable<FinanceAccount> AccessibleAccounts(Guid userId, Guid? fullWorthSpaceId) =>
        db.Accounts.AsNoTracking().Where(account =>
            (!fullWorthSpaceId.HasValue || account.FullWorthSpaceId == fullWorthSpaceId.Value) &&
            db.FullWorthSpaceMembers.Any(member =>
                member.FullWorthSpaceId == account.FullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId));

    private IQueryable<AccountListItem> Project(IQueryable<FinanceAccount> accounts) =>
        accounts.Select(account => new AccountListItem(
            account.Id, account.FullWorthSpaceId, account.BankConnectionId, account.InstitutionName, account.DisplayName,
            account.Product, account.AccountType, account.Currency, account.IbanLast4, account.IsActive,
            account.IncludeInNetWorth, account.SortOrder, account.UpdatedAt, account.Provider,
            // Inlined CurrentFirst ordering — this is a correlated subquery, where EF cannot expand the
            // extension (see BalanceSnapshotQueries.CurrentFirst). Newest capture, then rank-prefix + type.
            db.BalanceSnapshots.Where(balance => balance.AccountId == account.Id)
                .OrderByDescending(balance => balance.CapturedAt)
                .ThenBy(balance => (balance.BalanceType == "interimAvailable" ? "0"
                                  : balance.BalanceType == "closingAvailable" ? "1"
                                  : balance.BalanceType == "closingBooked" ? "2"
                                  : balance.BalanceType == "interimBooked" ? "3"
                                  : balance.BalanceType == "expected" ? "4" : "5") + balance.BalanceType)
                .Select(balance => new BalanceView(balance.Amount, balance.Currency, balance.BalanceType, balance.CapturedAt)).FirstOrDefault(),
            account.GroupId,
            // Inline scalar subquery (EF can't expand a helper inside a projection) — null when ungrouped.
            db.AccountGroups.Where(g => g.Id == account.GroupId).Select(g => g.Name).FirstOrDefault()));

    private static void ValidateCreateRequest(AccountCreateRequest request)
    {
        if (request.FullWorthSpaceId == Guid.Empty) throw new ArgumentException("FullWorth Space ID is required.");
        if (request.BankConnectionId == Guid.Empty) throw new ArgumentException("Bank connection ID must be omitted for manual accounts or reference an existing connection.");
        if (string.IsNullOrWhiteSpace(request.DisplayName)) throw new ArgumentException("Display name is required.");
        if (request.InitialBalance.HasValue) ValidateAmount(request.InitialBalance.Value);
        _ = NormalizeCurrency(request.Currency);
    }

    private static void ValidateAmount(decimal amount)
    {
        // BalanceSnapshot.Amount is numeric(20,8): the integral part caps below 10^12. Reject early
        // with a 400 instead of letting Postgres fail the insert with an opaque 500.
        if (Math.Abs(amount) >= 1_000_000_000_000m) throw new ArgumentException("Amount must be less than 1,000,000,000,000.");
    }

    private static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return "EUR";
        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must be a three-letter code.");
        return normalized;
    }

    private static void ApplySettings(FinanceAccount entity, AccountSettingsRequest request)
    {
        if (request.DisplayName is not null)
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName)) throw new ArgumentException("Display name cannot be empty.");
            entity.DisplayName = request.DisplayName.Trim();
        }
        if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;
        if (request.IncludeInNetWorth.HasValue) entity.IncludeInNetWorth = request.IncludeInNetWorth.Value;
        if (request.SortOrder.HasValue) entity.SortOrder = request.SortOrder.Value;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public enum ManualBalanceResult
{
    Ok,
    NotFound,
    Forbidden,
    NotManual
}

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        group.MapGet("/", async (Guid? fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
            Results.Ok(await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)));

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var item = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPost("/", async (AccountCreateRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                var item = await store.CreateForMemberAsync(currentUser.RequireUserId(), request, ct);
                return item is null
                    ? Results.NotFound()
                    : Results.Created($"/api/accounts/{item.Id}?fullWorthSpaceId={item.FullWorthSpaceId}", item);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPut("/{id:guid}/balance", async (Guid id, Guid fullWorthSpaceId, ManualBalanceRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.SetManualBalanceAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct) switch
                {
                    ManualBalanceResult.Ok => Results.NoContent(),
                    ManualBalanceResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                    ManualBalanceResult.NotManual => Results.Conflict(new { error = "Balances of synced accounts are managed by their bank connection." }),
                    _ => Results.NotFound()
                };
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // Assign (or clear, groupId=null) an account's group. Dedicated endpoint — PATCH's
        // null-means-unchanged settings semantics can't express "ungroup". Owner-gated like the balance PUT.
        group.MapPut("/{id:guid}/group", async (Guid id, Guid fullWorthSpaceId, AccountGroupAssignRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
            await store.AssignAccountToGroupAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request.GroupId, ct) switch
            {
                AccountGroupResult.Ok => Results.NoContent(),
                AccountGroupResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            });

        group.MapPatch("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, AccountSettingsRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (await store.GetForUserAsync(userId, fullWorthSpaceId, id, ct) is null) return Results.NotFound();
            if (!await store.HasEditAccessAsync(userId, fullWorthSpaceId, id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                return await store.UpdateSettingsForOwnerAsync(userId, fullWorthSpaceId, id, request, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (await store.GetForUserAsync(userId, fullWorthSpaceId, id, ct) is null) return Results.NotFound();
            if (!await store.HasEditAccessAsync(userId, fullWorthSpaceId, id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await store.ArchiveForOwnerAsync(userId, fullWorthSpaceId, id, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        group.MapGet("/{id:guid}/owners", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountService service, CancellationToken ct) =>
        {
            var owners = await service.ListOwnersAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return owners is null ? Results.NotFound() : Results.Ok(owners);
        });

        group.MapPost("/{id:guid}/owners", async (Guid id, Guid fullWorthSpaceId, AddAccountOwnerRequest request, CurrentUserContext currentUser, AccountService service, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await service.CanUserAccessAsync(userId, fullWorthSpaceId, id, ct)) return Results.NotFound();
            if (!await service.CanUserEditAsync(userId, fullWorthSpaceId, id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = await service.AddOwnerAsync(userId, fullWorthSpaceId, id, request.UserId, request.OwnershipType, ct);
            return result switch
            {
                AccountOwnerChangeResult.Added => Results.NoContent(),
                AccountOwnerChangeResult.TargetNotFullWorthSpaceMember or AccountOwnerChangeResult.NotFound => Results.NotFound(),
                AccountOwnerChangeResult.InvalidOwnershipType => Results.BadRequest(new { error = "Ownership type must be owner or viewer." }),
                AccountOwnerChangeResult.Duplicate => Results.Conflict(new { error = "The user already has account access." }),
                AccountOwnerChangeResult.AccessDenied => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.StatusCode(StatusCodes.Status409Conflict)
            };
        });

        group.MapDelete("/{id:guid}/owners/{targetUserId:guid}", async (Guid id, Guid targetUserId, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountService service, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await service.CanUserAccessAsync(userId, fullWorthSpaceId, id, ct)) return Results.NotFound();
            if (!await service.CanUserEditAsync(userId, fullWorthSpaceId, id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = await service.RemoveOwnerAsync(userId, fullWorthSpaceId, id, targetUserId, ct);
            return result switch
            {
                AccountOwnerChangeResult.Removed => Results.NoContent(),
                AccountOwnerChangeResult.NotFound => Results.NotFound(),
                AccountOwnerChangeResult.LastOwner => Results.Conflict(new { error = "The last account owner cannot be removed." }),
                AccountOwnerChangeResult.AccessDenied => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.StatusCode(StatusCodes.Status409Conflict)
            };
        });

        return app;
    }
}

public static class AccountGroupEndpoints
{
    public static IEndpointRouteBuilder MapAccountGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account-groups").WithTags("Accounts");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var result = await store.ListGroupsForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result.Found ? Results.Ok(result.Groups) : Results.NotFound();
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, AccountGroupWrite request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                var dto = await store.CreateGroupForMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return dto is null ? Results.NotFound() : Results.Created($"/api/account-groups/{dto.Id}?fullWorthSpaceId={dto.FullWorthSpaceId}", dto);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, AccountGroupWrite request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.RenameGroupForMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
            await store.DeleteGroupForMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)
                ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
