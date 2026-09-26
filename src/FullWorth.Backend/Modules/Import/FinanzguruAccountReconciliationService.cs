using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

public sealed record FinanzguruReconciliationResult(int AccountsReconciled, int TransactionsMoved, int TransactionsMerged);

public sealed record FinanzguruExplicitLinkResult(
    Guid ImportAccountId,
    Guid TargetAccountId,
    int TransactionsMoved,
    int TransactionsMerged,
    int TransactionsTrustedForHistory,
    bool CurrentBalanceAdded);

public sealed record FinanzguruImportAccountLinkView(
    Guid Id,
    string DisplayName,
    string Currency,
    string? IbanLast4,
    int TransactionCount,
    DateOnly? FirstBookingDate,
    DateOnly? LastBookingDate,
    Guid? SuggestedTargetAccountId,
    Guid? LinkedTargetAccountId);

public sealed record FinanzguruTargetAccountLinkView(
    Guid Id,
    string DisplayName,
    string InstitutionName,
    string Currency,
    string? IbanLast4,
    bool HasCurrentBalance,
    bool IsActive,
    bool IncludeInNetWorth);

public sealed record FinanzguruAttachedHistoryView(
    Guid TargetAccountId,
    string DisplayName,
    string InstitutionName,
    string Currency,
    int TransactionCount,
    DateOnly? FirstBookingDate,
    DateOnly? LastBookingDate,
    bool HasCurrentBalance);

/// <summary>Ein Paar, das dasselbe meint: eine importierte Zeile und eine des Zielkontos.</summary>
/// <param name="Kind">
/// Wie das Paar gefunden wurde: <c>exact</c> (gleicher Tag, Betrag, Name), <c>near</c> (gleicher Name,
/// bis zu drei Tage daneben) oder <c>probable</c> (gleicher Betrag bis zu drei Tage daneben, aber anders
/// benannt - Finanzguru und die Bank schreiben denselben Empfaenger oft verschieden).
/// </param>
public sealed record FinanzguruDuplicateMatchView(
    Guid ImportTransactionId,
    Guid TargetTransactionId,
    DateOnly? Date,
    decimal Amount,
    string Currency,
    string? Counterparty,
    string? ImportDescription,
    string? TargetDescription,
    string? ImportCategoryName,
    string? TargetCategoryName,
    string Kind = "exact",
    string? TargetCounterparty = null,
    DateOnly? TargetDate = null);

/// <summary>
/// Was das Zuordnen tun WUERDE, bevor es etwas tut: welche Zeilen zusammenfallen und welche als eigene
/// Buchungen umziehen. Ohne diese Liste war das Zuordnen ein Knopf, nach dem Buchungen verschwunden
/// waren, ohne dass jemand vorher sagen konnte, welche.
/// </summary>
public sealed record FinanzguruLinkPreviewView(
    IReadOnlyList<FinanzguruDuplicateMatchView> Matches,
    int MovedWithoutMatch);

public sealed record FinanzguruLinkOptionsView(
    IReadOnlyList<FinanzguruImportAccountLinkView> ImportAccounts,
    IReadOnlyList<FinanzguruTargetAccountLinkView> TargetAccounts,
    IReadOnlyList<FinanzguruAttachedHistoryView> AttachedHistory);

/// <summary>
/// Reattaches historical Finanzguru imports to a real bank account once that account is connected.
/// Matching is deliberately conservative: same FullWorthSpace, currency, IBAN last-4 and at least one
/// common account owner. Ambiguous matches are left untouched rather than risking a cross-account merge.
/// </summary>
public sealed class FinanzguruAccountReconciliationService(FullWorthDbContext db, AuditService audit)
{
    public const string ImportProvider = "finanzguru-import";

    public async Task<FinanzguruLinkOptionsView?> ListLinkOptionsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        var isMember = await db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);
        if (!isMember) return null;

        var importAccounts = await db.Accounts.AsNoTracking()
            .Where(account =>
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider == ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .OrderBy(account => account.DisplayName)
            .ToListAsync(ct);

        var targets = await db.Accounts.AsNoTracking()
            .Where(account =>
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider != ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .OrderByDescending(account => account.IsActive)
            .ThenBy(account => account.InstitutionName)
            .ThenBy(account => account.DisplayName)
            .ToListAsync(ct);

        var targetIds = targets.Select(account => account.Id).ToArray();
        var targetBalanceIds = (await db.BalanceSnapshots.AsNoTracking()
                .Where(balance => targetIds.Contains(balance.AccountId))
                .Select(balance => balance.AccountId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        var targetViews = targets.Select(account => new FinanzguruTargetAccountLinkView(
            account.Id,
            account.DisplayName,
            account.InstitutionName,
            account.Currency,
            account.IbanLast4,
            targetBalanceIds.Contains(account.Id),
            account.IsActive,
            account.IncludeInNetWorth)).ToArray();

        var importViews = new List<FinanzguruImportAccountLinkView>();
        foreach (var account in importAccounts)
        {
            var dates = await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.AccountId == account.Id)
                .Select(transaction => transaction.BookingDate ?? transaction.ValueDate)
                .ToListAsync(ct);
            var knownDates = dates.Where(date => date.HasValue).Select(date => date!.Value).ToArray();
            if (dates.Count == 0) continue;

            var matchingTargets = targets
                .Where(target =>
                    !ImportCurrency.Conflict(target.Currency, account.Currency) &&
                    !string.IsNullOrWhiteSpace(account.IbanLast4) &&
                    string.Equals(target.IbanLast4, account.IbanLast4, StringComparison.OrdinalIgnoreCase))
                .Select(target => target.Id)
                .ToList();
            var suggested = account.ImportLinkedAccountId
                ?? (matchingTargets.Count == 1 ? matchingTargets[0] : (Guid?)null);

            importViews.Add(new FinanzguruImportAccountLinkView(
                account.Id,
                account.DisplayName,
                account.Currency,
                account.IbanLast4,
                dates.Count,
                knownDates.Length == 0 ? null : knownDates.Min(),
                knownDates.Length == 0 ? null : knownDates.Max(),
                suggested,
                account.ImportLinkedAccountId));
        }

        var pendingDirectRows = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                targetIds.Contains(transaction.AccountId) &&
                transaction.ExternalKey.StartsWith("finanzguru:") &&
                !transaction.UseForBalanceHistory)
            .Select(transaction => new
            {
                transaction.AccountId,
                Date = transaction.BookingDate ?? transaction.ValueDate
            })
            .ToListAsync(ct);
        var targetsById = targets.ToDictionary(account => account.Id);
        var attached = pendingDirectRows
            .GroupBy(row => row.AccountId)
            .Select(group =>
            {
                var account = targetsById[group.Key];
                var dates = group.Where(row => row.Date.HasValue).Select(row => row.Date!.Value).ToArray();
                return new FinanzguruAttachedHistoryView(
                    account.Id,
                    account.DisplayName,
                    account.InstitutionName,
                    account.Currency,
                    group.Count(),
                    dates.Length == 0 ? null : dates.Min(),
                    dates.Length == 0 ? null : dates.Max(),
                    targetBalanceIds.Contains(account.Id));
            })
            .OrderBy(item => item.InstitutionName)
            .ThenBy(item => item.DisplayName)
            .ToArray();

        return new FinanzguruLinkOptionsView(importViews, targetViews, attached);
    }

    /// <summary>
    /// Dieselbe Paarung wie das Zuordnen selbst, nur ohne zu schreiben - und aus derselben Methode
    /// gespeist (<see cref="Signature"/>), damit die Liste nicht etwas anderes behauptet, als danach
    /// passiert.
    /// </summary>
    public async Task<FinanzguruLinkPreviewView?> PreviewLinkAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid importAccountId,
        Guid targetAccountId,
        CancellationToken ct)
    {
        if (importAccountId == targetAccountId) return null;
        var owns = await db.Accounts.AsNoTracking()
            .CountAsync(account =>
                (account.Id == importAccountId || account.Id == targetAccountId) &&
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner), ct);
        if (owns != 2) return null;

        var imported = await db.Transactions.AsNoTracking()
            .Where(transaction => transaction.AccountId == importAccountId)
            .OrderBy(transaction => transaction.BookingDate).ThenBy(transaction => transaction.Id)
            .ToListAsync(ct);
        var live = await db.Transactions.AsNoTracking()
            .Where(transaction => transaction.AccountId == targetAccountId
                                  && transaction.Status != "PDNG"
                                  && !transaction.ExternalKey.StartsWith("finanzguru:"))
            .OrderBy(transaction => transaction.BookingDate).ThenBy(transaction => transaction.Id)
            .ToListAsync(ct);

        var categories = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(category => category.Id, category => category.Name, ct);
        string? Name(Guid? id) => id.HasValue && categories.TryGetValue(id.Value, out var name) ? name : null;

        var pairs = Pair(imported, live, excluded: null);
        var matches = imported
            .Where(row => pairs.ContainsKey(row.Id))
            .Select(row =>
            {
                var (counterpart, kind) = pairs[row.Id];
                return new FinanzguruDuplicateMatchView(
                    row.Id, counterpart.Id, row.BookingDate ?? row.ValueDate, row.Amount, row.Currency,
                    row.Counterparty, row.Description, counterpart.Description,
                    Name(row.CategoryId), Name(counterpart.CategoryId),
                    kind, counterpart.Counterparty, counterpart.BookingDate ?? counterpart.ValueDate);
            })
            .ToList();

        return new FinanzguruLinkPreviewView(matches, imported.Count - matches.Count);
    }

    public async Task<FinanzguruReconciliationResult> ReconcileAsync(
        Guid fullWorthSpaceId,
        IEnumerable<FinanceAccount> candidateLiveAccounts,
        CancellationToken ct)
    {
        var liveAccounts = candidateLiveAccounts
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId
                              && account.Provider != ImportProvider)
            .GroupBy(account => account.Id)
            .Select(group => group.First())
            .ToList();
        if (liveAccounts.Count == 0) return new(0, 0, 0);

        var liveIds = liveAccounts.Select(account => account.Id).ToArray();
        var liveOwnerRows = await db.AccountOwners.AsNoTracking()
            .Where(owner => liveIds.Contains(owner.AccountId) && owner.OwnershipType == AccountOwnershipTypes.Owner)
            .Select(owner => new { owner.AccountId, owner.UserId })
            .ToListAsync(ct);
        var liveOwners = liveOwnerRows
            .GroupBy(row => row.AccountId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.UserId).ToHashSet());

        var importAccounts = await db.Accounts
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.Provider == ImportProvider)
            .Include(account => account.Owners)
            .ToListAsync(ct);

        var accountsReconciled = 0;
        var transactionsMoved = 0;
        var transactionsMerged = 0;

        foreach (var importedAccount in importAccounts)
        {
            // Hier wurde jedes Importkonto ohne eigenen Kontostand wieder archiviert und aus dem
            // Vermoegen genommen - bei JEDEM Sync JEDER Verbindung des Space. Ein Importkonto ist
            // jetzt von Anfang an ein richtiges Konto, und ob es sichtbar ist, entscheidet der
            // Nutzer, nicht der naechste Bankabruf. Stillgelegt wird nur noch das Konto, das eine
            // Zuordnung gerade ausgeraeumt hat - und das steht dort, wo es ausgeraeumt wird.
            if (importedAccount.ImportLinkedAccountId is null && string.IsNullOrWhiteSpace(importedAccount.IbanLast4)) continue;
            var importedOwners = importedAccount.Owners
                .Where(owner => owner.OwnershipType == AccountOwnershipTypes.Owner)
                .Select(owner => owner.UserId)
                .ToHashSet();
            if (importedOwners.Count == 0) continue;

            var matches = importedAccount.ImportLinkedAccountId is { } linkedId
                ? liveAccounts.Where(live =>
                        live.Id == linkedId &&
                        liveOwners.TryGetValue(live.Id, out var owners) &&
                        owners.Overlaps(importedOwners))
                    .ToList()
                : liveAccounts.Where(live =>
                        string.Equals(live.IbanLast4, importedAccount.IbanLast4, StringComparison.OrdinalIgnoreCase)
                        && !ImportCurrency.Conflict(live.Currency, importedAccount.Currency)
                        && liveOwners.TryGetValue(live.Id, out var owners)
                        && owners.Overlaps(importedOwners))
                    .ToList();
            if (matches.Count != 1) continue;

            var result = await ReconcileAccountAsync(importedAccount, matches[0], trustMovedHistory: false, ct);
            accountsReconciled++;
            transactionsMoved += result.Moved;
            transactionsMerged += result.Merged;

            // Die enge Fassung der Regel, die frueher jedes Importkonto ohne Kontostand traf: der
            // Abgleich nimmt JEDE Buchung dieses Kontos mit - zusammengefuehrt oder verschoben -, es
            // ist danach also leer. Ein leeres Konto in Kontenliste und Vermoegen waere eine Zeile
            // ueber nichts; es wird stillgelegt und merkt sich, wohin seine Historie ging.
            importedAccount.IsActive = false;
            importedAccount.IncludeInNetWorth = false;
            importedAccount.ImportLinkedAccountId = matches[0].Id;
            importedAccount.UpdatedAt = DateTimeOffset.UtcNow;

            audit.Record(fullWorthSpaceId, null, "finanzguru.account.reconciled", "FinanceAccount", matches[0].Id);
        }

        await db.SaveChangesAsync(ct);
        return new(accountsReconciled, transactionsMoved, transactionsMerged);
    }

    public async Task<FinanzguruExplicitLinkResult?> LinkExplicitAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid importAccountId,
        Guid targetAccountId,
        decimal? currentBalance,
        string? currentBalanceCurrency,
        CancellationToken ct,
        bool preferImport = false,
        IReadOnlySet<Guid>? excludedImportTransactionIds = null)
    {
        if (importAccountId == targetAccountId) return null;

        var importedAccount = await db.Accounts
            .Where(account =>
                account.Id == importAccountId &&
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider == ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .SingleOrDefaultAsync(ct);
        var targetAccount = await db.Accounts
            .Where(account =>
                account.Id == targetAccountId &&
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider != ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .SingleOrDefaultAsync(ct);
        if (importedAccount is null || targetAccount is null) return null;

        if (ImportCurrency.Conflict(importedAccount.Currency, targetAccount.Currency))
            throw new ArgumentException("Import and target account must use the same currency.");

        // Ein Konto ohne erklaerte Waehrung bekommt sie hier - aus dem Import, der eine hat. Sonst
        // bliebe "XXX" stehen und der naechste Vergleich scheiterte wieder an derselben Stelle.
        if (!ImportCurrency.IsDeclared(targetAccount.Currency) && ImportCurrency.IsDeclared(importedAccount.Currency))
            targetAccount.Currency = NormalizeCurrency(importedAccount.Currency);

        var balanceAdded = await EnsureCurrentBalanceAsync(
            targetAccount, currentBalance, currentBalanceCurrency, ct);

        var result = await ReconcileAccountAsync(
            importedAccount, targetAccount, trustMovedHistory: true, ct,
            preferImport, excludedImportTransactionIds);

        // Automatic reconciliation may already have moved imported rows onto this target account. Explicit
        // user confirmation upgrades every remaining Finanzguru row on the target to a trusted history source.
        var importedRowsOnTarget = await db.Transactions
            .Where(transaction =>
                transaction.AccountId == targetAccount.Id &&
                transaction.ExternalKey.StartsWith("finanzguru:"))
            .ToListAsync(ct);
        foreach (var row in importedRowsOnTarget)
        {
            row.UseForBalanceHistory = true;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        importedAccount.IsActive = false;
        importedAccount.IncludeInNetWorth = false;
        importedAccount.ImportLinkedAccountId = targetAccount.Id;
        importedAccount.UpdatedAt = DateTimeOffset.UtcNow;
        targetAccount.IncludeInNetWorth = true;
        targetAccount.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(fullWorthSpaceId, userId, "finanzguru.account.linked_explicitly", "FinanceAccount", targetAccount.Id);
        await db.SaveChangesAsync(ct);

        // Rows moved just now are already trusted for history (trustMovedHistory:true above), and
        // importedRowsOnTarget only sees rows a PRIOR automatic reconcile had moved (its query runs
        // before SaveChanges, so it cannot include the rows we just re-parented). The two sets are
        // disjoint, so the trusted-for-history total is the sum.
        return new FinanzguruExplicitLinkResult(
            importedAccount.Id,
            targetAccount.Id,
            result.Moved,
            result.Merged,
            result.Moved + importedRowsOnTarget.Count,
            balanceAdded);
    }

    public async Task<FinanzguruExplicitLinkResult?> ConfirmAttachedHistoryAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid targetAccountId,
        decimal? currentBalance,
        string? currentBalanceCurrency,
        CancellationToken ct)
    {
        var targetAccount = await db.Accounts
            .Where(account =>
                account.Id == targetAccountId &&
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.Provider != ImportProvider &&
                account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .SingleOrDefaultAsync(ct);
        if (targetAccount is null) return null;

        var balanceAdded = await EnsureCurrentBalanceAsync(targetAccount, currentBalance, currentBalanceCurrency, ct);
        var rows = await db.Transactions
            .Where(transaction =>
                transaction.AccountId == targetAccount.Id &&
                transaction.ExternalKey.StartsWith("finanzguru:") &&
                !transaction.UseForBalanceHistory)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.UseForBalanceHistory = true;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        targetAccount.IncludeInNetWorth = true;
        targetAccount.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "finanzguru.history.confirmed", "FinanceAccount", targetAccount.Id);
        await db.SaveChangesAsync(ct);

        return new FinanzguruExplicitLinkResult(
            Guid.Empty,
            targetAccount.Id,
            0,
            0,
            rows.Count,
            balanceAdded);
    }

    private async Task<bool> EnsureCurrentBalanceAsync(
        FinanceAccount targetAccount,
        decimal? currentBalance,
        string? currentBalanceCurrency,
        CancellationToken ct)
    {
        // Kein Kontostand ist kein Fehler mehr. Hier brach das Zuordnen ab, solange weder das
        // Zielkonto einen Stand hatte noch einer mitkam - das machte aus dem als optional
        // beschriebenen Feld eine Pflicht und liess eine saubere Zuordnung an etwas scheitern, das
        // man jederzeit nachtragen kann. Ohne Stand bleibt das Vermoegen unvollstaendig, und genau
        // das sagt es dem Nutzer auch, statt ihn hier aufzuhalten.
        if (!currentBalance.HasValue) return false;
        if (Math.Abs(currentBalance.Value) >= 1_000_000_000_000m)
            throw new ArgumentException("Current balance must be less than 1,000,000,000,000.");

        var currency = NormalizeCurrency(currentBalanceCurrency ?? targetAccount.Currency);
        if (ImportCurrency.Conflict(currency, targetAccount.Currency))
            throw new ArgumentException("Current balance currency must match the target account currency.");
        // Der eingetippte Saldo erklaert die Waehrung, wenn das Konto selbst keine hat.
        if (!ImportCurrency.IsDeclared(targetAccount.Currency)) targetAccount.Currency = currency;

        var now = DateTimeOffset.UtcNow;
        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = targetAccount.Id,
            Amount = currentBalance.Value,
            Currency = currency,
            BalanceType = "manualCurrent",
            // Typed by the owner while confirming an import, so it is their figure, not the file's.
            Source = BalanceSources.Manual,
            ReferenceDate = DateOnly.FromDateTime(now.UtcDateTime),
            CapturedAt = now
        });
        return true;
    }

    /// <param name="preferImport">
    /// Wessen Fassung gewinnt, wenn zwei Zeilen dasselbe meinen: die importierte oder die des
    /// Zielkontos. Gemeint ist der INHALT - Kategorie, Aufteilung, Notiz, Umbuchungs-Kennzeichen.
    ///
    /// Die IDENTITAET bleibt immer bei der Zeile des Zielkontos, und das ist keine Bequemlichkeit:
    /// eine Bankzeile traegt den Schluessel, an dem die Bank sie wiedererkennt. Wer sie loeschte,
    /// bekaeme sie beim naechsten Abruf neu geliefert - das Doppel waere zurueck, nur ohne die Arbeit,
    /// die daran hing.
    /// </param>
    /// <param name="excludedImportTransactionIds">
    /// Zeilen, die der Nutzer von der Zusammenfuehrung ausgenommen hat. Sie wandern mit auf das
    /// Zielkonto, bleiben dort aber eigene Buchungen.
    /// </param>
    private async Task<(int Moved, int Merged)> ReconcileAccountAsync(
        FinanceAccount importedAccount,
        FinanceAccount liveAccount,
        bool trustMovedHistory,
        CancellationToken ct,
        bool preferImport = false,
        IReadOnlySet<Guid>? excludedImportTransactionIds = null)
    {
        var imported = await db.Transactions
            .Where(transaction => transaction.AccountId == importedAccount.Id)
            .OrderBy(transaction => transaction.BookingDate)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync(ct);
        if (imported.Count == 0) return (0, 0);

        var live = await db.Transactions
            .Where(transaction => transaction.AccountId == liveAccount.Id
                                  && transaction.Status != "PDNG"
                                  && !transaction.ExternalKey.StartsWith("finanzguru:"))
            .OrderBy(transaction => transaction.BookingDate)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync(ct);
        var pairs = Pair(imported, live, excludedImportTransactionIds);

        var moved = 0;
        var merged = 0;
        // Die Zeilen, die EF hier verfolgt. Das Umhaengen laeuft in rohem SQL, und ein Verweis, den
        // eine dieser Zeilen noch auf die Verliererzeile haelt, waere danach veraltet - sie werden
        // darum in derselben Bewegung mitgezogen.
        var tracked = imported.Concat(live).ToList();
        var connection = await RawSql.OpenAsync(db, ct);
        foreach (var historical in imported)
        {
            var counterpart = pairs.TryGetValue(historical.Id, out var pair) ? pair.Live : null;
            if (counterpart is not null
                && await TransactionMergeService.CanMergeAsync(db, historical.Id, counterpart.Id, ct))
            {
                await MergeIntoLiveTransactionAsync(historical, counterpart, tracked, preferImport, ct);
                merged++;
            }
            else
            {
                // Zwei Gruende, beide enden gleich: entweder liefert die Bank diesen Zeitraum gar
                // nicht, oder beide Seiten tragen eine eigene Aufteilung - die liesse sich nicht
                // zusammenlegen, ohne dass der Gewinner doppelt so viel aufgeteilt haette, wie er
                // wert ist. Die Importzeile bleibt also stehen und wandert nur auf das echte Konto;
                // ein spaeterer, breiterer Banksync kann sie immer noch zusammenfuehren.
                historical.AccountId = liveAccount.Id;
                historical.UseForBalanceHistory = trustMovedHistory;
                historical.UpdatedAt = DateTimeOffset.UtcNow;
                moved++;

                // Der Herkunftsnachweis faellt hier bewusst weg: die Buchung lebt jetzt auf einem
                // echten, aktiv genutzten Konto, und ein Nutzer, der spaeter den urspruenglichen
                // Finanzguru-Import zurueckrollt, darf diese Buchung nicht mehr verlieren - genau das
                // haette ohne diese Zeile passieren koennen (#175). Der zusammengefuehrte Zweig oben
                // bekommt dasselbe Ergebnis bereits ueber die CASCADE der geloeschten Verliererzeile.
                await using var unlink = RawSql.Command(connection,
                    "DELETE FROM \"ImportTransactionLinks\" WHERE \"TransactionId\"=@transaction",
                    ("@transaction", historical.Id));
                await unlink.ExecuteNonQueryAsync(ct);
            }
        }

        return (moved, merged);
    }

    /// <summary>
    /// Fuehrt die importierte Zeile in die Bankzeile zusammen: erst gewinnt der Inhalt nach der Regel
    /// unten, dann wandert JEDE Beziehung mit, dann faellt die Importzeile.
    ///
    /// Hier standen vier Schleifen fuer vier Beziehungen. Sechzehn Fremdschluessel zeigen auf eine
    /// Buchung, und die uebrigen zwoelf verhielten sich nach ihrer eigenen Regel: Schlagworte,
    /// Pruefzustaende, Vertragszuordnung und Ausgaben-Reviews fielen per CASCADE weg, ein Asset-
    /// Cashflow oder ein Beleg-Zahlungslink liess die Zusammenfuehrung mit einer
    /// Fremdschluesselverletzung platzen. <see cref="TransactionMergeService"/> haelt die Liste jetzt
    /// an einer Stelle, und <c>TransactionMergeGuardTests</c> haelt sie gegen das Schema.
    /// </summary>
    /// <param name="tracked">
    /// Buchungen, die EF in diesem Durchlauf verfolgt. Das Umhaengen laeuft in rohem SQL, ihre
    /// Verweise auf die Verliererzeile muessen also im Speicher mitgezogen werden, sonst schreibt der
    /// naechste SaveChanges den alten Stand zurueck.
    /// </param>
    private async Task MergeIntoLiveTransactionAsync(
        FinanceTransaction historical,
        FinanceTransaction live,
        IReadOnlyCollection<FinanceTransaction> tracked,
        bool preferImport,
        CancellationToken ct)
    {
        var historicalHasAllocations = await db.TransactionAllocations.AsNoTracking()
            .AnyAsync(allocation => allocation.TransactionId == historical.Id, ct);

        // Manual user edits always win. Otherwise retain the imported Finanzguru category/split when the
        // bank row has only automatic/no categorization.
        //
        // Hat der Nutzer beim Zuordnen ausdruecklich die importierte Fassung gewaehlt, gilt sie
        // vollstaendig - dieselbe Uebernahme wie bei einer manuellen Bearbeitung, nur weil er es so
        // gesagt hat und nicht, weil die Zeile es von sich aus behauptet.
        if (preferImport || historical.CategorizationSource == "manual")
        {
            live.CategoryId = historical.CategoryId;
            live.IsIgnored = historical.IsIgnored;
            live.IsTransfer = historical.IsTransfer;
            live.TransferPurpose = historical.TransferPurpose;
            live.UserNote = historical.UserNote;
            // Die Herkunft der Kategorie bleibt ehrlich: "manual" nur, wenn sie wirklich von Hand
            // kam. Hat der Nutzer bloss die importierte Fassung gewaehlt, ist sie weiterhin die des
            // Imports.
            live.CategorizationSource = historical.CategorizationSource == "manual"
                ? "manual"
                : historical.CategorizationSource;
        }
        else if (live.CategorizationSource != "manual")
        {
            if (historical.CategoryId.HasValue) live.CategoryId = historical.CategoryId;
            if (historical.CategorizationSource == "finanzguru" && (historical.CategoryId.HasValue || historicalHasAllocations))
                live.CategorizationSource = "finanzguru";
            if (historical.IsTransfer) live.IsTransfer = true;
            if (string.IsNullOrWhiteSpace(live.UserNote) && !string.IsNullOrWhiteSpace(historical.UserNote))
                live.UserNote = historical.UserNote;
        }

        live.UseForBalanceHistory = true;
        live.UpdatedAt = DateTimeOffset.UtcNow;

        // Verweise der verfolgten Zeilen mitziehen, BEVOR das rohe SQL laeuft - danach stimmen
        // Speicher und Datenbank ueberein und das UPDATE unten findet dort nichts mehr zu tun.
        foreach (var other in tracked)
            if (other.RefundOfTransactionId == historical.Id && other.Id != live.Id)
                other.RefundOfTransactionId = live.Id;

        // Den bisherigen Stand festschreiben, damit das rohe Umhaengen nicht an verfolgten, noch
        // nicht geschriebenen Aenderungen vorbeilaeuft.
        await db.SaveChangesAsync(ct);
        await TransactionMergeService.MoveDependenciesAsync(db, historical.Id, live.Id, ct);

        db.Transactions.Remove(historical);
        await db.SaveChangesAsync(ct);
    }

    private static TransactionSignature Signature(FinanceTransaction transaction) => new(
        transaction.BookingDate ?? transaction.ValueDate,
        transaction.Amount,
        transaction.Currency.ToUpperInvariant(),
        transaction.NormalizedCounterparty ?? MerchantNormalization.Normalize(transaction.Counterparty) ?? NormalizeDescription(transaction.Description));

    private static string NormalizeCurrency(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must be a three-letter code.");
        return normalized;
    }

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    /// <summary>
    /// Welche importierte Zeile welche Bankzeile meint - eine Stelle fuer die Vorschau und das
    /// Zusammenfuehren, damit die Liste sagt, was danach passiert.
    ///
    /// Drei Durchgaenge ueber ALLE Zeilen, jeder nur mit dem, was der vorige uebrig liess - so nimmt nie
    /// eine ungefaehre Uebereinstimmung einer genauen die Bankzeile weg:
    /// <list type="number">
    /// <item><c>exact</c>: gleicher Tag, Betrag, Waehrung und Name.</item>
    /// <item><c>near</c>: gleicher Name, bis zu <see cref="MatchToleranceDays"/> Tage daneben - Bank und
    /// Export nennen oft den Buchungs- und den Wertstellungstag.</item>
    /// <item><c>probable</c>: gleicher Betrag und gleiche Waehrung im selben Fenster, aber anders benannt.
    /// Finanzguru schreibt "Moebelhaus Beispiel GmbH", die Bank "MOEBELHAUS BEISPIEL MUSTERSTADT", und
    /// ohne diesen Durchgang zog die Zeile als zweite Buchung mit um: dasselbe Geld zweimal. Uebrig
    /// bleiben dafuer nur Zeilen, die im Zeitraum beider Quellen auf keiner Seite ein Gegenstueck mit
    /// gleichem Namen haben - dort ist "gleicher Betrag, gleiche Tage" fast immer dieselbe Buchung. Die
    /// Vorschau zeigt diese Paare ausdruecklich, und wer eines abwaehlt, behaelt beide Zeilen.</item>
    /// </list>
    /// Je Durchgang gewinnt die zeitlich naechste Bankzeile.
    /// </summary>
    private static Dictionary<Guid, (FinanceTransaction Live, string Kind)> Pair(
        IReadOnlyList<FinanceTransaction> imported, IReadOnlyList<FinanceTransaction> live, IReadOnlySet<Guid>? excluded)
    {
        var pairs = new Dictionary<Guid, (FinanceTransaction Live, string Kind)>();
        var consumed = new HashSet<Guid>();
        var open = imported.Where(row => excluded?.Contains(row.Id) != true).ToList();

        // 1. exakt
        var bySignature = live.GroupBy(Signature).ToDictionary(group => group.Key, group => new Queue<FinanceTransaction>(group));
        foreach (var row in open)
        {
            if (!bySignature.TryGetValue(Signature(row), out var queue)) continue;
            while (queue.Count > 0)
            {
                var candidate = queue.Dequeue();
                if (!consumed.Add(candidate.Id)) continue;
                pairs[row.Id] = (candidate, "exact");
                break;
            }
        }

        // 2. und 3. im Fenster - erst mit gleichem Namen, dann nur nach Betrag. Die Bankzeilen einmal
        // nach Betrag und Waehrung abgelegt: sonst normalisierte jeder Vergleich beide Namen neu.
        var byAmount = live
            .Select(candidate => (Row: candidate, Signature: Signature(candidate)))
            .GroupBy(entry => (entry.Signature.Amount, entry.Signature.Currency))
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var (kind, sameParty) in new[] { ("near", true), ("probable", false) })
        {
            foreach (var row in open.Where(row => !pairs.ContainsKey(row.Id)))
            {
                var signature = Signature(row);
                if (signature.Date is not { } day) continue;
                if (!byAmount.TryGetValue((signature.Amount, signature.Currency), out var sameAmount)) continue;
                var best = sameAmount
                    .Where(entry => !consumed.Contains(entry.Row.Id)
                                    && (!sameParty || entry.Signature.Party == signature.Party)
                                    && entry.Signature.Date is { } date
                                    && Math.Abs(date.DayNumber - day.DayNumber) <= MatchToleranceDays)
                    .OrderBy(entry => Math.Abs(entry.Signature.Date!.Value.DayNumber - day.DayNumber))
                    .ThenBy(entry => entry.Row.Id)
                    .Select(entry => entry.Row)
                    .FirstOrDefault();
                if (best is null) continue;
                consumed.Add(best.Id);
                pairs[row.Id] = (best, kind);
            }
        }
        return pairs;
    }

    private sealed record TransactionSignature(DateOnly? Date, decimal Amount, string Currency, string? Party);

    /// <summary>
    /// Wie weit zwei Buchungen auseinanderliegen duerfen und trotzdem dieselbe sind. Bank und Export
    /// nennen oft verschiedene Tage fuer denselben Vorgang - die eine das Buchungs-, die andere das
    /// Wertstellungsdatum -, und ein Tag Unterschied machte aus einer Buchung zwei.
    /// Drei Tage decken das Wochenende mit ab; darueber hinaus faengt man an zu raten.
    /// </summary>
    private const int MatchToleranceDays = 3;
}
