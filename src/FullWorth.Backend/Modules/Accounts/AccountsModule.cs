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
    // Import archive accounts can be explicitly and persistently mapped to their canonical account.
    // Null for ordinary accounts and for imports that have not been confirmed by the user yet.
    // NOTE: this is a MERGE marker, not a counting one - FinanzguruAccountReconciliationService MOVES
    // the archive's bookings onto the target and keeps doing so on every sync. It is therefore not the
    // home for "these two accounts are the same"; that is DuplicateOfAccountId below, which never
    // touches a booking.
    public Guid? ImportLinkedAccountId { get; set; }
    public string InstitutionName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Product { get; set; }
    public string? AccountType { get; set; }
    public string? Usage { get; set; }
    public string? PsuStatus { get; set; }
    public decimal? CreditLimitAmount { get; set; }
    public string? CreditLimitCurrency { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? IbanLast4 { get; set; }
    // Keyed lookup token for exact transfer matching. The full IBAN is never persisted.
    public string? IbanLookup { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IncludeInNetWorth { get; set; } = true;

    /// <summary>
    /// The account the owner declared this one to be the same real-world account as. Set only by an
    /// explicit user decision, never by a sync, and deliberately independent of the IBAN: PayPal, Wise,
    /// Revolut, cash and manual accounts have no IBAN, so every identity check keyed on that token
    /// (IbanLookup) can never see them as duplicates of anything.
    ///
    /// It is a statement about COUNTING, not a data merge: the linked account keeps every booking and
    /// balance it has and stays visible, it is only left out of the totals.
    /// </summary>
    public Guid? DuplicateOfAccountId { get; set; }

    /// <summary>
    /// What <see cref="IncludeInNetWorth"/> was immediately before the link was made, so unlinking
    /// restores the previous state instead of guessing "true". An account that the automatic IBAN rule
    /// had already excluded at creation goes back to excluded, not to counted.
    /// </summary>
    public bool? IncludeInNetWorthBeforeLink { get; set; }
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

    /// <summary>
    /// Where this figure came from - see <see cref="BalanceSources"/>. A balance said what it was (the
    /// bank's balance_type) but never where it came from, so a value the owner typed and a value a bank
    /// reported looked identical on screen. That matters most where there is no bank at all.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>The owner's own remark, e.g. which statement or app the figure was read off.</summary>
    public string? Note { get; set; }

    /// <summary>The date the figure is valid FOR, as opposed to when it was recorded.</summary>
    public DateOnly? ReferenceDate { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The provenance of a balance. Stored, never guessed from the balance type.</summary>
public static class BalanceSources
{
    /// <summary>Reported by a bank through a live connection.</summary>
    public const string Provider = "provider";

    /// <summary>Entered by the owner.</summary>
    public const string Manual = "manual";

    /// <summary>Read off an imported file (a statement, or an import wizard's anchor).</summary>
    public const string Import = "import";
}

// ReferenceDate is the as-of date, CapturedAt when FullWorth recorded it. Both are shown: an owner
// who anchors an account with last month's statement needs to see that it is last month's figure.
public sealed record BalanceView(
    decimal Amount,
    string Currency,
    string BalanceType,
    DateTimeOffset CapturedAt,
    DateOnly? ReferenceDate = null,
    string? Source = null,
    string? Note = null);

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
//
// LatestBalance is the HEADLINE figure only. Balances carries the account's current balance in every
// currency it holds - PayPal, Wise and Revolut report a wallet per currency - and it is what any total
// must be built from. It used to be one row per account, so every other wallet was invisible.
// DuplicateOf* names the account this one is the same bank account as, reached through another
// provider (same IBAN) or declared so by the owner. Both connections are kept on purpose - each brings
// data the other does not - so the row has to say why one of them is not in the totals.
// DuplicateLinkExplicit separates the two sources: only a link the owner made can be taken back, and
// the row's actions must not offer "unlink" for a plain IBAN match.
public sealed record AccountListItem(Guid Id, Guid FullWorthSpaceId, Guid? BankConnectionId, string InstitutionName, string DisplayName, string? Product, string? AccountType, string Currency, string? IbanLast4, bool IsActive, bool IncludeInNetWorth, int SortOrder, DateTimeOffset UpdatedAt, string Provider, BalanceView? LatestBalance, Guid? GroupId = null, string? GroupName = null, decimal? BaseValue = null, string? BaseCurrency = null, IReadOnlyList<BalanceView>? Balances = null, Guid? DuplicateOfAccountId = null, string? DuplicateOfDisplayName = null, bool DuplicateLinkExplicit = false);
public sealed record AccountCreateRequest(Guid FullWorthSpaceId, Guid? BankConnectionId, string DisplayName, string? Currency, bool? IncludeInNetWorth, int? SortOrder, string? InstitutionName = null, decimal? InitialBalance = null);
public sealed record AccountSettingsRequest(string? DisplayName, bool? IsActive, bool? IncludeInNetWorth, int? SortOrder);
// AsOf defaults to today when omitted, so an existing caller keeps its behaviour. Note is the
// owner's remark about where the figure came from.
public sealed record ManualBalanceRequest(decimal Amount, string? Currency, DateOnly? AsOf = null, string? Note = null);
public sealed record AccountGroupDto(Guid Id, Guid FullWorthSpaceId, string Name, int SortOrder);
public sealed record AccountGroupWrite(string Name, int? SortOrder);
public sealed record AccountGroupAssignRequest(Guid? GroupId);
public enum AccountGroupResult { Ok, NotFound, Forbidden }

// --- Explicit "these two accounts are the same" link (O-4 / O-5) ---

/// <summary>The account this one is to be counted as. Any account of the space qualifies, wallets and
/// cash included - the link is deliberately not keyed on an IBAN.</summary>
public sealed record AccountLinkRequest(Guid DuplicateOfAccountId);

/// <summary>A candidate for the picker: every other account of the space the caller owns.</summary>
public sealed record AccountLinkCandidate(
    Guid Id,
    string DisplayName,
    string InstitutionName,
    string Currency,
    string? IbanLast4,
    bool IsActive,
    bool IncludeInNetWorth,
    bool IsLinked);

/// <summary>
/// What the owner needs to decide: whether this account is already linked (and by whom - a decision of
/// theirs or the automatic IBAN rule), which accounts declare themselves the same as this one, and what
/// can be picked.
/// </summary>
public sealed record AccountLinkState(
    Guid AccountId,
    Guid? DuplicateOfAccountId,
    string? DuplicateOfDisplayName,
    bool Explicit,
    IReadOnlyList<AccountLinkCandidate> Candidates,
    IReadOnlyList<AccountLinkCandidate> LinkedToThis);

public enum AccountLinkResult
{
    Ok,
    NotFound,
    Forbidden,
    /// <summary>The picked account is itself declared a duplicate of a third one.</summary>
    TargetIsLinked,
    /// <summary>Other accounts are already counted as this one, so it cannot become a duplicate itself.</summary>
    IsLinkTarget,
    /// <summary>The picked account is out of the totals, so linking would count the money zero times.</summary>
    TargetNotCounted,
    /// <summary>Unlink on an account that carries no explicit link.</summary>
    NotLinked
}

public sealed class AccountStore(FullWorthDbContext db, AuditService? auditService = null, FullWorth.Backend.Modules.Fx.CurrencyConverter? fx = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    public async Task<List<AccountListItem>> ListForUserAsync(Guid userId, Guid? fullWorthSpaceId, CancellationToken ct)
    {
        var items = await Project(AccessibleAccounts(userId, fullWorthSpaceId)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.InstitutionName).ThenBy(x => x.DisplayName))
            .ToListAsync(ct);
        items = await WithAllCurrenciesAsync(items, ct);
        items = await WithConvertedBalancesAsync(items, fullWorthSpaceId, ct);
        items = await WithDuplicateMarkersAsync(items, ct);
        return WithDisplayIdentifiers(items);
    }

    // The same bank account reached through two providers (same IBAN) is two accounts by design - each
    // connection brings data the other does not - but only one of them counts in the totals. The row
    // has to name the other one, or a user seeing an account excluded from their net worth has no way
    // to tell why.
    //
    // Two sources, in this order of authority:
    //   1. DuplicateOfAccountId - the owner said so. It works for every account, wallets and cash
    //      included, because it is not derived from an identifier those accounts do not have.
    //   2. The IbanLookup match - the automatic rule, only ever a fallback. A user decision wins.
    private async Task<List<AccountListItem>> WithDuplicateMarkersAsync(
        List<AccountListItem> items, CancellationToken ct)
    {
        var excluded = items.Where(item => !item.IncludeInNetWorth).Select(item => item.Id).ToArray();
        if (excluded.Length == 0) return items;

        var ids = items.Select(item => item.Id).ToArray();
        // IbanLookup is a keyed token, never the IBAN itself, and never leaves the server.
        var identities = await db.Accounts.AsNoTracking()
            .Where(account => ids.Contains(account.Id))
            .Select(account => new
            {
                account.Id,
                account.IbanLookup,
                account.IncludeInNetWorth,
                account.DuplicateOfAccountId
            })
            .ToListAsync(ct);

        var explicitLinks = identities
            .Where(account => !account.IncludeInNetWorth && account.DuplicateOfAccountId is not null)
            .ToDictionary(account => account.Id, account => account.DuplicateOfAccountId!.Value);
        var counterpartByAccount = identities
            .Where(account => !account.IncludeInNetWorth &&
                              account.DuplicateOfAccountId is null &&
                              account.IbanLookup != null)
            .Select(account => new
            {
                account.Id,
                Counterpart = identities.FirstOrDefault(other =>
                    other.Id != account.Id &&
                    other.IncludeInNetWorth &&
                    other.IbanLookup != null &&
                    other.IbanLookup == account.IbanLookup)
            })
            .Where(pair => pair.Counterpart is not null)
            .ToDictionary(pair => pair.Id, pair => pair.Counterpart!.Id);
        if (explicitLinks.Count == 0 && counterpartByAccount.Count == 0) return items;

        var nameById = items.ToDictionary(item => item.Id, item => item.DisplayName);
        // A link may point at an account outside this result set (one the caller does not own, or one
        // hidden by a space filter). The marker still has to name it, or the row reads as excluded for
        // no reason at all.
        var unnamed = explicitLinks.Values.Where(id => !nameById.ContainsKey(id)).Distinct().ToArray();
        if (unnamed.Length > 0)
            foreach (var row in await db.Accounts.AsNoTracking()
                         .Where(account => unnamed.Contains(account.Id))
                         .Select(account => new { account.Id, account.DisplayName })
                         .ToListAsync(ct))
                nameById[row.Id] = row.DisplayName;

        return items.Select(item =>
        {
            if (explicitLinks.TryGetValue(item.Id, out var linked))
                return item with
                {
                    DuplicateOfAccountId = linked,
                    DuplicateOfDisplayName = nameById.GetValueOrDefault(linked),
                    DuplicateLinkExplicit = true
                };
            return counterpartByAccount.TryGetValue(item.Id, out var counterpart)
                ? item with
                {
                    DuplicateOfAccountId = counterpart,
                    DuplicateOfDisplayName = nameById.GetValueOrDefault(counterpart)
                }
                : item;
        }).ToList();
    }

    // An account can hold money in several currencies. The projection above can only carry one row per
    // account (a correlated subquery cannot return a set), so the full per-currency picture is attached
    // here - and the headline balance is re-picked from it deterministically instead of depending on
    // which row the subquery happened to order first.
    private async Task<List<AccountListItem>> WithAllCurrenciesAsync(
        List<AccountListItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return items;
        var balances = await CurrentBalances.LoadAsync(db, items.Select(item => item.Id).ToArray(), ct);
        if (balances.Count == 0) return items;
        var byAccount = balances.GroupBy(balance => balance.AccountId)
            .ToDictionary(group => group.Key, group => group.ToList());
        return items.Select(item =>
        {
            if (!byAccount.TryGetValue(item.Id, out var rows)) return item;
            var primary = CurrentBalances.Primary(rows, item.Currency);
            var ordered = rows
                .OrderByDescending(balance => primary is not null && balance.Currency == primary.Currency)
                .ThenByDescending(balance => balance.Amount)
                .ThenBy(balance => balance.Currency, StringComparer.Ordinal)
                .Select(balance => new BalanceView(
                    balance.Amount, balance.Currency, balance.BalanceType, balance.CapturedAt,
                    balance.ReferenceDate, balance.Source, balance.Note))
                .ToList();
            return item with
            {
                LatestBalance = ordered[0],
                Balances = ordered
            };
        }).ToList();
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
            // The whole account, every currency it holds - this is what a group subtotal and the net
            // worth add up. Converting only the headline wallet left the rest of a multi-currency
            // account out of every base-currency figure on screen.
            var balances = item.Balances ?? (item.LatestBalance is null ? [] : [item.LatestBalance]);
            if (balances.Count == 0) return item;
            if (balances.All(balance =>
                    string.Equals(balance.Currency, baseCurrency, StringComparison.OrdinalIgnoreCase)))
                return item;  // already the base currency: the native amount IS the base amount

            decimal total = 0m;
            foreach (var balance in balances)
            {
                var converted = snapshot.ToBaseOn(balance.Amount, balance.Currency, today);
                // A missing rate makes the total unknown, not smaller: the row then shows only its
                // native amounts, exactly as it did before any conversion existed.
                if (converted is null) return item;
                total += converted.Value;
            }
            return item with { BaseValue = total, BaseCurrency = baseCurrency };
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
        // Same per-currency treatment as the list; see WithAllCurrenciesAsync.
        var item = await Project(AccessibleAccounts(userId, fullWorthSpaceId).Where(x => x.Id == accountId))
            .SingleOrDefaultAsync(ct);
        if (item is null) return null;
        var withCurrencies = await WithAllCurrenciesAsync([item], ct);
        return WithDisplayIdentifiers(withCurrencies)[0];
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
                Source = BalanceSources.Manual,
                ReferenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CapturedAt = now
            };
            db.BalanceSnapshots.Add(snapshot);
            initialBalance = new BalanceView(
                snapshot.Amount, snapshot.Currency, snapshot.BalanceType, snapshot.CapturedAt,
                snapshot.ReferenceDate, snapshot.Source, snapshot.Note);
        }

        await db.SaveChangesAsync(ct);

        var created = new AccountListItem(
            account.Id, account.FullWorthSpaceId, account.BankConnectionId, account.InstitutionName, account.DisplayName,
            account.Product, account.AccountType, account.Currency, account.IbanLast4, account.IsActive,
            account.IncludeInNetWorth, account.SortOrder, account.UpdatedAt, account.Provider, initialBalance);
        return WithDisplayIdentifiers([created])[0];
    }

    /// <summary>
    /// Records a new balance snapshot for an account the owner maintains by hand. Only account owners
    /// may set it, and only on accounts with no bank connection — anything tied to a connection gets its
    /// balances from that connection. Ordering mirrors PATCH/DELETE: not-found → forbidden → conflict.
    ///
    /// The gate is the CONNECTION, not the provider label. It used to be Provider == "manual", which
    /// locked out the one other connection-less kind: a finanzguru-import account. That account could
    /// then only ever show a value by linking it to a different, live account after the import - and the
    /// link was undone by the next sync. An imported account must be able to carry its own balance.
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
        if (account.BankConnectionId is not null) return ManualBalanceResult.NotManual;
        if (account.Provider is not ("manual" or FinanzguruImportProvider)) return ManualBalanceResult.NotManual;

        // A snapshot in a different currency would silently corrupt net worth: the aggregation sums
        // the latest snapshot per account bucketed by the ACCOUNT's currency.
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? account.Currency : NormalizeCurrency(request.Currency);
        if (currency != account.Currency) throw new ArgumentException("Currency must match the account currency.");

        // The as-of date is the owner's, not the clock's: anchoring an account from last month's
        // statement is a figure valid for last month. A future date is not a balance anyone can have
        // seen yet, so it is refused rather than quietly clamped.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var asOf = request.AsOf ?? today;
        if (asOf > today) throw new ArgumentException("The as-of date cannot be in the future.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 200 }) throw new ArgumentException("Note must be 200 characters or fewer.");

        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = accountId,
            Amount = request.Amount,
            Currency = currency,
            BalanceType = "manual",
            Source = BalanceSources.Manual,
            Note = note,
            ReferenceDate = asOf,
            CapturedAt = DateTimeOffset.UtcNow
        });

        // An imported history account is created archived and excluded, because the export file carries
        // no balance. Anchoring it with one makes it a real account: leaving it hidden would show the
        // history as a flat line and keep the money out of net worth, which is the whole complaint.
        // Its bookings also have to count towards the balance history, exactly as confirming an
        // attached history does for a live account.
        if (account.Provider == FinanzguruImportProvider)
        {
            var imported = await db.Accounts.SingleAsync(x => x.Id == accountId, ct);
            imported.IsActive = true;
            imported.IncludeInNetWorth = true;
            imported.UpdatedAt = DateTimeOffset.UtcNow;

            var bookings = await db.Transactions
                .Where(transaction => transaction.AccountId == accountId && !transaction.UseForBalanceHistory)
                .ToListAsync(ct);
            foreach (var booking in bookings)
            {
                booking.UseForBalanceHistory = true;
                booking.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
        return ManualBalanceResult.Ok;
    }

    /// <summary>The provider of a Finanzguru history import; see FinanzguruImportService.</summary>
    private const string FinanzguruImportProvider = "finanzguru-import";

    // --- Explicit "these two accounts are the same" link (O-4 / O-5) ---
    //
    // Every automatic path that decides two accounts are the same is keyed on IbanLookup: the ingest's
    // count-once rule and the duplicate marker above. PayPal, Wise, Revolut, cash and manual accounts
    // have no IBAN, so for them that decision could never be made at all - and even where an IBAN
    // exists it was made once, at creation, with no way for the owner to make it or take it back.
    //
    // The link below is that missing decision. It is a statement about COUNTING only: no booking and no
    // balance is moved or deleted, the linked account stays visible and keeps everything it has, it is
    // just out of the totals. Unlinking puts IncludeInNetWorth back to the stored previous value.

    /// <summary>
    /// Everything the owner needs to link this account: its current link (and whether that link is
    /// theirs or the automatic IBAN rule's), the accounts that are already counted as this one, and the
    /// candidates. Candidates are deliberately unfiltered by type or IBAN - a PayPal wallet, a cash
    /// account and a Girokonto are all valid answers to "which account is this really?".
    /// </summary>
    public async Task<AccountLinkState?> GetLinkStateAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct)
    {
        var accounts = await AccessibleAccounts(userId, fullWorthSpaceId)
            .Select(account => new LinkRow(
                account.Id, account.DisplayName, account.InstitutionName, account.Currency,
                account.IbanLast4, account.IsActive, account.IncludeInNetWorth,
                account.DuplicateOfAccountId))
            .ToListAsync(ct);
        var self = accounts.SingleOrDefault(account => account.Id == accountId);
        if (self is null) return null;

        var candidates = accounts
            .Where(account => account.Id != accountId)
            .OrderByDescending(account => account.IsActive)
            .ThenBy(account => account.InstitutionName, StringComparer.CurrentCulture)
            .ThenBy(account => account.DisplayName, StringComparer.CurrentCulture)
            .Select(ToCandidate)
            .ToList();
        var linkedToThis = accounts
            .Where(account => account.DuplicateOfAccountId == accountId)
            .OrderBy(account => account.DisplayName, StringComparer.CurrentCulture)
            .Select(ToCandidate)
            .ToList();

        var duplicateOf = self.DuplicateOfAccountId;
        string? duplicateOfName = duplicateOf is null
            ? null
            : accounts.FirstOrDefault(account => account.Id == duplicateOf.Value)?.DisplayName
              ?? await db.Accounts.AsNoTracking()
                  .Where(account => account.Id == duplicateOf.Value)
                  .Select(account => account.DisplayName)
                  .FirstOrDefaultAsync(ct);

        return new AccountLinkState(
            accountId, duplicateOf, duplicateOfName, duplicateOf is not null, candidates, linkedToThis);
    }

    /// <summary>
    /// Declares <paramref name="accountId"/> the same real-world account as <paramref name="targetId"/>
    /// and takes it out of the totals. Owner-gated, and ordered exactly like the manual-balance write:
    /// not-found → forbidden → conflict.
    /// </summary>
    public async Task<AccountLinkResult> LinkAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, Guid targetId, CancellationToken ct)
    {
        if (targetId == Guid.Empty) throw new ArgumentException("The account to count this one as is required.");
        if (targetId == accountId) throw new ArgumentException("An account cannot be linked to itself.");

        var account = await FindOwnAccountAsync(userId, fullWorthSpaceId, accountId, ct);
        if (account is null) return AccountLinkResult.NotFound;
        if (!await IsOwnerAsync(userId, accountId, ct)) return AccountLinkResult.Forbidden;

        var target = await FindOwnAccountAsync(userId, fullWorthSpaceId, targetId, ct);
        if (target is null) return AccountLinkResult.NotFound;

        // No chains and no cycles: a duplicate never becomes someone else's original, and an original
        // never becomes a duplicate. Both ends have to be resolved by the owner first, so there is
        // always exactly one account that carries the money.
        if (target.DuplicateOfAccountId is not null) return AccountLinkResult.TargetIsLinked;
        if (await db.Accounts.AsNoTracking().AnyAsync(other => other.DuplicateOfAccountId == accountId, ct))
            return AccountLinkResult.IsLinkTarget;

        // "Counted once" means once, not zero times. Linking into an account that is itself out of the
        // totals would make the money disappear from net worth with nothing on screen saying so.
        if (!target.IncludeInNetWorth) return AccountLinkResult.TargetNotCounted;

        if (account.DuplicateOfAccountId == targetId) return AccountLinkResult.Ok;

        // Only remember the pre-link state on the FIRST link. Re-pointing an existing link must not
        // overwrite it with the "false" this account already carries because of that link.
        account.IncludeInNetWorthBeforeLink ??= account.IncludeInNetWorth;
        account.DuplicateOfAccountId = targetId;
        account.IncludeInNetWorth = false;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "account.linked_as_duplicate", "Account", accountId);
        await db.SaveChangesAsync(ct);
        return AccountLinkResult.Ok;
    }

    /// <summary>
    /// Takes the owner's link back and restores <see cref="FinanceAccount.IncludeInNetWorth"/> to the
    /// value stored when the link was made. Nothing else changes - there was never anything to undo,
    /// because linking moved no data.
    /// </summary>
    public async Task<AccountLinkResult> UnlinkAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct)
    {
        var account = await FindOwnAccountAsync(userId, fullWorthSpaceId, accountId, ct);
        if (account is null) return AccountLinkResult.NotFound;
        if (!await IsOwnerAsync(userId, accountId, ct)) return AccountLinkResult.Forbidden;
        if (account.DuplicateOfAccountId is null) return AccountLinkResult.NotLinked;

        account.DuplicateOfAccountId = null;
        // Stored, never guessed. An account the IBAN rule had already excluded at creation goes back to
        // excluded; one that was counted goes back to counted.
        account.IncludeInNetWorth = account.IncludeInNetWorthBeforeLink ?? true;
        account.IncludeInNetWorthBeforeLink = null;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "account.duplicate_link_removed", "Account", accountId);
        await db.SaveChangesAsync(ct);
        return AccountLinkResult.Ok;
    }

    private sealed record LinkRow(
        Guid Id, string DisplayName, string InstitutionName, string Currency, string? IbanLast4,
        bool IsActive, bool IncludeInNetWorth, Guid? DuplicateOfAccountId);

    private static AccountLinkCandidate ToCandidate(LinkRow account) => new(
        account.Id, account.DisplayName, account.InstitutionName, account.Currency,
        account.IbanLast4, account.IsActive, account.IncludeInNetWorth,
        account.DuplicateOfAccountId is not null);

    private Task<FinanceAccount?> FindOwnAccountAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct) =>
        db.Accounts.SingleOrDefaultAsync(x =>
            x.Id == accountId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            x.Owners.Any(owner => owner.UserId == userId), ct);

    private Task<bool> IsOwnerAsync(Guid userId, Guid accountId, CancellationToken ct) =>
        db.Set<AccountOwner>().AsNoTracking().AnyAsync(x =>
            x.AccountId == accountId && x.UserId == userId && x.OwnershipType == AccountOwnershipTypes.Owner, ct);

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
                .Select(balance => new BalanceView(
                    balance.Amount, balance.Currency, balance.BalanceType, balance.CapturedAt,
                    balance.ReferenceDate, balance.Source, balance.Note)).FirstOrDefault(),
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
        if (request.IncludeInNetWorth.HasValue)
        {
            entity.IncludeInNetWorth = request.IncludeInNetWorth.Value;
            // "Count this one after all" and "this one is a duplicate of that one" are the same decision
            // stated two ways. Keeping the link while switching the account back on would leave the row
            // counted AND marked as not counted, so the link goes with it.
            if (request.IncludeInNetWorth.Value && entity.DuplicateOfAccountId is not null)
            {
                entity.DuplicateOfAccountId = null;
                entity.IncludeInNetWorthBeforeLink = null;
            }
        }
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

        // The explicit, reversible "these two accounts are the same" link (O-4/O-5). Owner-gated and
        // ordered like the balance PUT: not-found → forbidden → conflict. No IBAN anywhere in sight, so
        // it works for PayPal, Wise, Revolut, cash and manual accounts too.
        group.MapGet("/{id:guid}/link", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var state = await store.GetLinkStateAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return state is null ? Results.NotFound() : Results.Ok(state);
        });

        group.MapPut("/{id:guid}/link", async (Guid id, Guid fullWorthSpaceId, AccountLinkRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.LinkAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request.DuplicateOfAccountId, ct) switch
                {
                    AccountLinkResult.Ok => Results.NoContent(),
                    AccountLinkResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                    AccountLinkResult.TargetIsLinked => Results.Conflict(new { error = "The chosen account is itself linked to another one. Link to that one instead." }),
                    AccountLinkResult.IsLinkTarget => Results.Conflict(new { error = "Other accounts are already counted as this one. Remove those links first." }),
                    AccountLinkResult.TargetNotCounted => Results.Conflict(new { error = "The chosen account is excluded from net worth, so linking would count the money nowhere." }),
                    _ => Results.NotFound()
                };
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/{id:guid}/link", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
            await store.UnlinkAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct) switch
            {
                AccountLinkResult.Ok => Results.NoContent(),
                AccountLinkResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                AccountLinkResult.NotLinked => Results.Conflict(new { error = "This account is not linked to another one." }),
                _ => Results.NotFound()
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
