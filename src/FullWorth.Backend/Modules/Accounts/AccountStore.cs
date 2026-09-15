using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

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

        // Ein Konto ohne Gruppe gibt es seit #125 nicht mehr; ohne Angabe ist es die Standardgruppe.
        account.GroupId = await DefaultGroupIdAsync(request.FullWorthSpaceId, ct);

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
            // Der Name, den die Bank dem Konto gibt (#125): die Uebersicht zeigt ihn, wo Platz ist, die
            // Kontodetailseite immer.
            account.ProviderDisplayName, account.Product, account.AccountType, account.Currency, account.IbanLast4, account.IsActive,
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
        await EnsureDefaultGroupAsync(fullWorthSpaceId, ct);
        var groups = await db.AccountGroups.AsNoTracking()
            .Where(g => g.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
            .Select(g => new AccountGroupDto(g.Id, g.FullWorthSpaceId, g.Name, g.SortOrder, g.IsDefault))
            .ToListAsync(ct);
        return (true, groups);
    }

    /// <summary>
    /// Sorgt dafuer, dass der Space genau eine Standardgruppe hat und kein Konto ohne Gruppe dasteht.
    ///
    /// Das geschieht beim Lesen und nicht in einer Migration, weil der Name an der Sprache des Space
    /// haengt - und weil ein Space auch ohne den Seeder entstehen kann. Geschrieben wird nur, wenn
    /// wirklich etwas fehlt; im Normalfall sind es zwei Abfragen, die nichts finden.
    /// </summary>
    private async Task EnsureDefaultGroupAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var standard = await db.AccountGroups
            .SingleOrDefaultAsync(g => g.FullWorthSpaceId == fullWorthSpaceId && g.IsDefault, ct);

        if (standard is null)
        {
            // Eine eigene Gruppe, nie eine vorhandene dazu erklaert. Wer schon Gruppen angelegt hat, soll
            // nicht erleben, dass eine davon ploetzlich unloeschbar ist, weil sie zufaellig die erste war.
            var language = await db.FullWorthSpaces.AsNoTracking()
                .Where(space => space.Id == fullWorthSpaceId)
                .Select(space => space.DefaultCategoryLanguage)
                .SingleOrDefaultAsync(ct);
            standard = new AccountGroup
            {
                FullWorthSpaceId = fullWorthSpaceId,
                Name = DefaultGroupName(language),
                SortOrder = 0,
                IsDefault = true
            };
            db.AccountGroups.Add(standard);
        }

        var orphans = await db.Accounts
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.GroupId == null)
            .ToListAsync(ct);
        foreach (var account in orphans) account.GroupId = standard.Id;

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    private static string DefaultGroupName(string? language) =>
        (language ?? "de").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "General" : "Allgemein";

    /// <summary>Die Standardgruppe des Space, nachdem sichergestellt wurde, dass es sie gibt.</summary>
    private async Task<Guid> DefaultGroupIdAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        await EnsureDefaultGroupAsync(fullWorthSpaceId, ct);
        return await db.AccountGroups.AsNoTracking()
            .Where(g => g.FullWorthSpaceId == fullWorthSpaceId && g.IsDefault)
            .Select(g => g.Id)
            .SingleAsync(ct);
    }

    public async Task<AccountGroupDto?> CreateGroupForMemberAsync(Guid userId, Guid fullWorthSpaceId, AccountGroupWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null; // → 404
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("Group name is required.");
        var group = new AccountGroup { FullWorthSpaceId = fullWorthSpaceId, Name = name, SortOrder = request.SortOrder ?? 0 };
        db.AccountGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return new AccountGroupDto(group.Id, group.FullWorthSpaceId, group.Name, group.SortOrder, group.IsDefault);
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
        // Die Standardgruppe ersatzlos zu loeschen hiesse, Konten wieder gruppenlos zu machen - genau den
        // Zustand, den es seit #125 nicht mehr gibt.
        if (group.IsDefault) throw new ArgumentException("The default group cannot be deleted.");

        // Der Fremdschluessel setzt die Gruppe sonst auf NULL. Die Konten gehoeren in die Standardgruppe,
        // damit sie nicht in einem Eimer landen, den es nicht mehr gibt.
        var fallback = await DefaultGroupIdAsync(fullWorthSpaceId, ct);
        var moved = await db.Accounts
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.GroupId == groupId)
            .ToListAsync(ct);
        foreach (var account in moved) account.GroupId = fallback;

        db.AccountGroups.Remove(group);
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

        // Keine Gruppe zu waehlen heisst seit #125: die Standardgruppe. Ein Konto ganz ohne Gruppe
        // gibt es nicht mehr, und die Kontenuebersicht haette keine Zeile, unter der es stehen koennte.
        account.GroupId = groupId ?? await DefaultGroupIdAsync(fullWorthSpaceId, ct);
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
            // Der Name, den die Bank dem Konto gibt (#125): die Uebersicht zeigt ihn, wo Platz ist, die
            // Kontodetailseite immer.
            account.ProviderDisplayName, account.Product, account.AccountType, account.Currency, account.IbanLast4, account.IsActive,
            account.IncludeInNetWorth, account.SortOrder, account.UpdatedAt, account.Provider,
            // No balance here, on purpose. This used to be a correlated subquery carrying a hand-written
            // copy of the balance-type preference, because EF cannot expand a method inside a projection
            // lambda — and WithAllCurrenciesAsync then threw the result away and re-picked the headline
            // through CurrentBalances anyway. Both callers of Project() run it, so the copy bought
            // nothing and was one more place the preference could drift. Every path now reads the one
            // rule in CurrentBalances.
            null,
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
