using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

public sealed record FinanzguruImportResult(
    int SourceRows,
    int TransactionsImported,
    int AlreadyImported,
    int MatchedExistingTransactions,
    /// <summary>
    /// Vorhandene Buchungen, denen dieser Import Kategorie, Aufteilung oder Umbuchungskennzeichnung
    /// nachgetragen hat (#131, Abschnitt 6/7). Teilmenge von <paramref name="MatchedExistingTransactions"/>:
    /// ein Treffer, an dem schon jemand gearbeitet hat, bleibt unberuehrt.
    /// </summary>
    int EnrichedExistingTransactions,
    int AccountsMatched,
    int AccountsCreated,
    int CategoriesCreated,
    int CategoriesMatched,
    int CategoriesUnmapped,
    int SplitTransactions);

public sealed class FinanzguruImportConflictException(string message) : Exception(message);

public sealed record FinanzguruExplicitLinkRequest(
    Guid TargetAccountId,
    decimal? CurrentBalance,
    string? CurrentBalanceCurrency,
    // Wessen Fassung gewinnt, wo zwei Zeilen dasselbe meinen - der Inhalt, nicht die Zeile selbst.
    // Die Bankzeile bleibt immer bestehen: sie traegt den Schluessel, an dem die Bank sie
    // wiedererkennt, und geloescht kaeme sie beim naechsten Abruf einfach zurueck.
    bool PreferImport = false,
    // Treffer, die der Nutzer in der Vorschau abgewaehlt hat. Sie ziehen als eigene Buchungen um.
    IReadOnlyList<Guid>? ExcludedImportTransactionIds = null);

public sealed record FinanzguruConfirmHistoryRequest(
    decimal? CurrentBalance,
    string? CurrentBalanceCurrency);

public sealed class FinanzguruImportService(
    FullWorthDbContext db,
    FinanzguruWorkbookReader reader,
    AuditService audit,
    FieldCipher cipher,
    AccountStore accounts)
{
    private const string Provider = "finanzguru-import";

    /// <summary>
    /// Der Schluessel, unter dem dieser Import in der gemeinsamen Auftragsliste steht. Er sagt, WOHER
    /// die Datei kam - nicht, was fuer ein Konto daraus wird (#131).
    /// </summary>
    private const string AdapterKey = "finanzguru_xlsx";

    /// <summary>
    /// Legt den Auftrag an, der diesen Import in der gemeinsamen Liste sichtbar und rueckgaengig
    /// machbar macht - VOR den Buchungen, nicht danach. "TransactionAllocations.CreatedByImportJobId"
    /// (Splits, #175) und "ImportJobCreatedAccounts" (Konten) zeigen per Fremdschluessel auf diese
    /// Zeile, und beide koennen schon waehrend des Imports selbst entstehen - die Zeile muss also
    /// zuerst da sein. Die Zaehlerspalten bekommen ihren echten Wert erst danach, in
    /// <see cref="FinalizeImportJobCountsAsync"/>: erst wenn die Schleife durch ist, steht fest, wie
    /// viele Zeilen neu und wie viele schon vorhanden waren.
    ///
    /// Zeilen (<c>ImportCandidates</c>) legt DIESE Fassung keine an: sie ist der Weg ohne Halt, bei
    /// dem Hochladen gleich Festschreiben heisst, und eine Kandidatenzeile ohne Entscheidung waere eine
    /// Behauptung ueber einen Schritt, den es hier nicht gab. Den Weg MIT Vorschau legt
    /// <see cref="FinanzguruStagingService"/> an, und der bringt seinen Auftrag samt Zeilen mit.
    /// </summary>
    private async Task CreateImportJobAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, string? fileSha, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        await using var command = RawSql.Command(connection, """
INSERT INTO "ImportJobs" ("Id","FullWorthSpaceId","UserId","FileName","FileSha256","AdapterKey","Status",
                          "SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount",
                          "CreatedAt","UpdatedAt","CompletedAt")
VALUES (@id,@space,@uid,@name,@sha,@adapter,'completed',0,0,0,0,0,@now,@now,@now)
""",
            ("@id", jobId), ("@space", fullWorthSpaceId), ("@uid", userId),
            // Ohne Datei (der Weg ueber Zeilen, den die Tests nehmen) steht die Auftragskennung dort -
            // nie eine erfundene Pruefsumme, die eine Datei behaupten wuerde, die es nicht gab.
            ("@name", "finanzguru.xlsx"), ("@sha", fileSha ?? jobId.ToString("N")), ("@adapter", AdapterKey), ("@now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Traegt nach, wie viele Zeilen neu und wie viele schon vorhanden waren.</summary>
    private async Task FinalizeImportJobCountsAsync(
        Guid jobId, int sourceRows, int imported, int duplicates, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
UPDATE "ImportJobs"
-- GREATEST, damit ein Festschreiben aus der Vorschau die Zeilenzahl der DATEI nicht durch die
-- Zahl der gewaehlten Zeilen ersetzt: "312 Zeilen, 40 uebernommen" ist die Auskunft, "40 von 40"
-- waere eine andere und falsche (#131).
SET "SourceRowCount"=GREATEST("SourceRowCount",@source),"ReadyCount"=@imported,"ImportedCount"=@imported,
    "DuplicateCount"=@duplicates,"Status"='completed',"CompletedAt"=@now,"UpdatedAt"=@now
WHERE "Id"=@id
""",
            ("@id", jobId), ("@source", sourceRows), ("@imported", imported), ("@duplicates", duplicates),
            ("@now", DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<FinanzguruImportResult?> ImportAsync(Guid userId, Guid fullWorthSpaceId, Stream workbook, CancellationToken ct)
    {
        // Die Pruefsumme der Datei gehoert in den Auftrag (#131): sie ist es, woran sich spaeter
        // erkennen laesst, dass dieselbe Datei schon einmal da war.
        using var buffer = new MemoryStream();
        await workbook.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var sha = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        buffer.Position = 0;
        var rows = reader.Read(buffer);
        return await ImportRowsAsync(userId, fullWorthSpaceId, rows, ct, sha);
    }

    /// <param name="existingJobId">
    /// Der Auftrag, den die Vorschau schon angelegt hat (#131). Ohne ihn entstuende beim
    /// Festschreiben ein ZWEITER Auftrag fuer dieselbe Datei - und die Kandidatenzeilen, die der
    /// Nutzer gerade abgewaehlt hat, haengten am ersten.
    /// </param>
    public async Task<FinanzguruImportResult?> ImportRowsAsync(
        Guid userId, Guid fullWorthSpaceId, IReadOnlyList<FinanzguruRow> rows, CancellationToken ct,
        string? fileSha = null, Guid? existingJobId = null)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return null;

        ValidateSplits(rows);
        var parentRows = rows.Where(row => row.SplitType is null or "Original").ToList();
        var splitChildren = rows.Where(row => row.SplitType is "Teilbuchung" or "Restbetrag")
            .GroupBy(row => row.OriginalReferenceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Der gemeinsame Auftrag (#131). Der Finanzguru-Import war der einzige Dateiimport ohne einen:
        // er schrieb Buchungen direkt, ohne Herkunftsverknuepfung - und damit gab es keinen Weg, ihn
        // rueckgaengig zu machen. Die Konten laesst dieser Schritt bewusst unangetastet; sie sind
        // Teil (c) des Umbaus und brauchen ihre eigene Migration.
        var jobId = existingJobId ?? Guid.NewGuid();
        var createdTransactionIds = new List<Guid>();

        // Zuerst die Zeile in "ImportJobs" selbst - "TransactionAllocations.CreatedByImportJobId" und
        // "ImportJobCreatedAccounts" zeigen per Fremdschluessel darauf und koennen schon in der
        // Konto- bzw. Buchungsschleife unten entstehen.
        // Den Auftrag gibt es schon, wenn die Vorschau ihn angelegt hat - dann wird er hier nur
        // abgeschlossen statt neu erfunden.
        if (existingJobId is null) await CreateImportJobAsync(userId, fullWorthSpaceId, jobId, fileSha, ct);

        var accounts = await ResolveAccountsAsync(userId, fullWorthSpaceId, parentRows, ct);
        var categoryResolver = await CategoryResolver.CreateAsync(db, fullWorthSpaceId, role == FullWorthSpaceRoles.Owner, ct);

        var imported = 0;
        var alreadyImported = 0;
        var matchedExisting = 0;
        var enrichedExisting = 0;
        var splitTransactions = 0;
        var enrichments = new List<ImportedEnrichment>();
        var now = DateTimeOffset.UtcNow;

        foreach (var accountGroup in parentRows.GroupBy(row => AccountKey(row), StringComparer.Ordinal))
        {
            var resolved = accounts.BySourceKey[accountGroup.Key];
            var sourceRows = accountGroup.ToList();
            var externalKeys = sourceRows.Select(row => ExternalKey(row.BookingId)).Distinct(StringComparer.Ordinal).ToArray();
            var existingByKey = await db.Transactions
                .Where(item => item.AccountId == resolved.Account.Id && externalKeys.Contains(item.ExternalKey))
                .ToDictionaryAsync(item => item.ExternalKey, StringComparer.Ordinal, ct);

            Dictionary<TransactionSignature, Queue<FinanceTransaction>> semanticMatches = [];
            HashSet<Guid> allocatedTransactions = [];
            if (resolved.MatchedLiveAccount)
            {
                var minDate = sourceRows.Min(row => row.BookingDate);
                var maxDate = sourceRows.Max(row => row.BookingDate);
                var existingRows = await db.Transactions
                    .Where(item => item.AccountId == resolved.Account.Id
                                   && item.BookingDate >= minDate && item.BookingDate <= maxDate
                                   && item.Status != "PDNG"
                                   && !item.ExternalKey.StartsWith("finanzguru:"))
                    .ToListAsync(ct);
                semanticMatches = existingRows
                    .GroupBy(Signature)
                    .ToDictionary(group => group.Key, group => new Queue<FinanceTransaction>(group.OrderBy(item => item.Id)));

                // Eine Buchung mit Aufteilungen hat jemand geteilt - das ist eine Entscheidung, und
                // sie steht nicht an der Buchung selbst, sondern in einer zweiten Tabelle. Ohne diese
                // Abfrage waere "noch nichts entschieden" eine Behauptung ueber etwas Ungelesenes.
                var existingIds = existingRows.Select(item => item.Id).ToArray();
                allocatedTransactions = existingIds.Length == 0
                    ? []
                    : (await db.TransactionAllocations
                        .Where(item => existingIds.Contains(item.TransactionId))
                        .Select(item => item.TransactionId)
                        .Distinct()
                        .ToListAsync(ct)).ToHashSet();
            }

            foreach (var row in sourceRows)
            {
                var key = ExternalKey(row.BookingId);
                if (existingByKey.ContainsKey(key))
                {
                    alreadyImported++;
                    continue;
                }

                if (resolved.MatchedLiveAccount
                    && semanticMatches.TryGetValue(Signature(row), out var queue)
                    && queue.Count > 0)
                {
                    // Die Bank hat diese Buchung schon geliefert. Was Finanzguru zusaetzlich weiss -
                    // Kategorie, Aufteilung, Umbuchung - war bis hierher gezaehlt und weggeworfen
                    // (#131, Abschnitt 6/7). Jetzt kommt es an der vorhandenen Buchung an, aber nur
                    // dort, wo noch niemand etwas entschieden hat.
                    var existing = queue.Dequeue();
                    matchedExisting++;
                    if (EnrichExisting(existing, row, splitChildren, categoryResolver,
                            allocatedTransactions, jobId, now, enrichments))
                    {
                        enrichedExisting++;
                        if (splitChildren.ContainsKey(row.BookingId)) splitTransactions++;
                    }
                    continue;
                }

                var children = splitChildren.GetValueOrDefault(row.BookingId) ?? [];
                var categoryId = children.Count == 0
                    ? categoryResolver.Resolve(row.MainCategory, row.SubCategory)
                    : null;

                var entity = new FinanceTransaction
                {
                    AccountId = resolved.Account.Id,
                    CategoryId = categoryId,
                    ExternalKey = key,
                    ProviderTransactionId = row.BookingId,
                    Status = "BOOK",
                    BookingDate = row.BookingDate,
                    ValueDate = row.BookingDate,
                    Amount = row.Amount,
                    Currency = row.Currency,
                    Counterparty = row.Counterparty,
                    NormalizedCounterparty = MerchantNormalization.Normalize(row.Counterparty),
                    Description = row.Description,
                    EntryReference = row.EntryReference,
                    IsTransfer = row.IsTransfer,
                    IsIgnored = false,
                    UseForBalanceHistory = false,
                    CategorizationSource = categoryId.HasValue || children.Count > 0 ? "finanzguru" : "none",
                    RawJson = cipher.Protect(JsonSerializer.Serialize(new
                    {
                        source = "finanzguru",
                        row = row.RawValues,
                        splits = children.Select(child => child.RawValues).ToArray()
                    })) ?? "{}",
                    FirstSeenAt = now,
                    UpdatedAt = now
                };
                db.Transactions.Add(entity);
                createdTransactionIds.Add(entity.Id);
                existingByKey[key] = entity;

                if (children.Count > 0)
                {
                    splitTransactions++;
                    foreach (var child in children)
                    {
                        db.TransactionAllocations.Add(new TransactionAllocation
                        {
                            TransactionId = entity.Id,
                            CategoryId = categoryResolver.Resolve(child.MainCategory, child.SubCategory),
                            Amount = child.Amount,
                            // Diese Aufteilung ist das eigene Ergebnis DIESES Imports, keine
                            // Nutzerarbeit - eine spaetere Ruecknahme darf nicht daran scheitern. Siehe
                            // ImportTransactionProvenance.DeleteImportedTransactionsSql.
                            CreatedByImportJobId = jobId,
                            CreatedAt = now,
                            UpdatedAt = now
                        });
                    }
                }

                imported++;
            }
        }

        audit.Record(fullWorthSpaceId, userId, "finanzguru.imported", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);

        // Erst jetzt stehen die echten Zahlen fest - wie viele Zeilen neu waren und wie viele schon
        // vorhanden. Die Verknuepfung braucht ausserdem die Kennungen der eben geschriebenen Buchungen.
        await FinalizeImportJobCountsAsync(jobId, rows.Count, imported, alreadyImported, ct);
        await ImportTransactionProvenance.LinkAsync(db, jobId, createdTransactionIds, ct);
        await ImportTransactionEnrichment.RecordAsync(db, jobId, enrichments, ct);
        await ImportJobAccountProvenance.LinkCreatedAccountsAsync(db, jobId, accounts.CreatedAccountIds, ct);

        await transaction.CommitAsync(ct);

        return new FinanzguruImportResult(
            rows.Count,
            imported,
            alreadyImported,
            matchedExisting,
            enrichedExisting,
            accounts.Matched,
            accounts.Created,
            categoryResolver.Created,
            categoryResolver.Matched,
            categoryResolver.Unmapped,
            splitTransactions);
    }

    /// <summary>
    /// Traegt an einer vorhandenen Buchung nach, was die Quelle zusaetzlich weiss (#131, Abschnitt 6/7).
    ///
    /// Ergaenzt wird als Paket oder gar nicht: eine Buchung, an der schon irgendetwas entschieden
    /// ist, bleibt unberuehrt. Damit gibt es hier keinen Fall, in dem etwas ueberschrieben wird -
    /// die Regel dafuer steht in <see cref="ImportTransactionEnrichment.MayEnrich"/> und gilt auch
    /// fuer die Vorschau.
    /// </summary>
    /// <returns>Ob tatsaechlich etwas nachgetragen wurde.</returns>
    private bool EnrichExisting(
        FinanceTransaction existing,
        FinanzguruRow row,
        IReadOnlyDictionary<string, List<FinanzguruRow>> splitChildren,
        CategoryResolver categoryResolver,
        HashSet<Guid> allocatedTransactions,
        Guid jobId,
        DateTimeOffset now,
        List<ImportedEnrichment> enrichments)
    {
        if (!ImportTransactionEnrichment.MayEnrich(
                existing.CategoryId, existing.CategorizationSource, existing.IsTransfer,
                allocatedTransactions.Contains(existing.Id)))
            return false;

        var children = splitChildren.GetValueOrDefault(row.BookingId) ?? [];
        var categoryId = children.Count == 0
            ? categoryResolver.Resolve(row.MainCategory, row.SubCategory)
            : null;
        // Ohne Kategorie, ohne Aufteilung und ohne Umbuchung hat die Quelle nichts beizutragen. Eine
        // Notiz darueber waere eine ueber nichts.
        if (categoryId is null && children.Count == 0 && !row.IsTransfer) return false;

        existing.CategoryId = categoryId;
        existing.CategorizationSource = categoryId.HasValue || children.Count > 0 ? "finanzguru" : "none";
        if (row.IsTransfer) existing.IsTransfer = true;
        existing.UpdatedAt = now;

        foreach (var child in children)
        {
            db.TransactionAllocations.Add(new TransactionAllocation
            {
                TransactionId = existing.Id,
                CategoryId = categoryResolver.Resolve(child.MainCategory, child.SubCategory),
                Amount = child.Amount,
                CreatedByImportJobId = jobId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        enrichments.Add(new ImportedEnrichment(existing.Id, categoryId, row.IsTransfer));
        return true;
    }

    /// <summary>
    /// Welches Konto eine Quellzeile MEINT - ohne eines anzulegen (#131, Schritt 3).
    ///
    /// Die Vorschau muss dieselbe Frage beantworten wie das Festschreiben, sonst zeigt sie etwas
    /// anderes, als danach passiert. Deshalb steht die Zuordnungsregel genau hier, einmal, und
    /// <see cref="ResolveAccountsAsync"/> benutzt sie: es haengt nur das Anlegen an, wo nichts passt.
    ///
    /// <c>null</c> heisst "dafuer gibt es noch kein Konto" - beim Festschreiben entsteht dort eines,
    /// in der Vorschau steht dort "wird angelegt".
    /// </summary>
    internal async Task<Dictionary<string, MatchedAccount>> MatchAccountsAsync(
        Guid userId, Guid fullWorthSpaceId, IReadOnlyList<FinanzguruRow> rows, CancellationToken ct)
    {
        var sourceGroups = rows.GroupBy(AccountKey, StringComparer.Ordinal).ToList();
        var hashes = sourceGroups.Select(group => IdentificationHash(group.Key)).ToArray();
        var importAccounts = await db.Accounts.AsNoTracking()
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.Provider == Provider && hashes.Contains(account.IdentificationHash))
            .Include(account => account.Owners)
            .ToDictionaryAsync(account => account.IdentificationHash, StringComparer.Ordinal, ct);
        var ownedAccounts = await db.Accounts.AsNoTracking()
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId
                              && account.Provider != Provider
                              && account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .ToListAsync(ct);

        var result = new Dictionary<string, MatchedAccount>(StringComparer.Ordinal);
        foreach (var group in sourceGroups)
        {
            var sample = group.First();
            importAccounts.TryGetValue(IdentificationHash(group.Key), out var importedAccount);

            // A user-confirmed link is authoritative and survives later re-imports, including imports
            // whose source account has no usable IBAN. Without such a link, retain the conservative
            // unique-IBAN heuristic for convenient automatic matching.
            var normalizedReference = NormalizeAccountReference(sample.ReferenceAccount);
            var ibanLast4 = LooksLikeIban(normalizedReference) ? normalizedReference[^4..] : null;
            FinanceAccount? liveMatch = importedAccount?.ImportLinkedAccountId is { } linkedId
                ? ownedAccounts.SingleOrDefault(account =>
                    account.Id == linkedId &&
                    // Eine bestaetigte Verknuepfung ist massgeblich - der Kommentar darueber sagt es,
                    // und ein Gleichheitsvergleich nahm sie ihr wieder weg, sobald das Konto keine
                    // Waehrung erklaert (#112).
                    !ImportCurrency.Conflict(account.Currency, sample.Currency))
                : null;
            if (liveMatch is null && ibanLast4 is not null)
            {
                var candidates = ownedAccounts
                    .Where(account => string.Equals(account.IbanLast4, ibanLast4, StringComparison.OrdinalIgnoreCase)
                                      && !ImportCurrency.Conflict(account.Currency, sample.Currency))
                    .ToList();
                if (candidates.Count == 1) liveMatch = candidates[0];
            }

            result[group.Key] = liveMatch is not null
                ? new MatchedAccount(liveMatch, true, false)
                : importedAccount is not null
                    ? new MatchedAccount(importedAccount, false, false)
                    : new MatchedAccount(null, false, true);
        }
        return result;
    }

    /// <summary>Ein Konto, das eine Quellzeile meint - oder die Feststellung, dass es noch keines gibt.</summary>
    internal sealed record MatchedAccount(FinanceAccount? Account, bool MatchedLiveAccount, bool WouldBeCreated);

    /// <summary>
    /// Dasselbe wie <see cref="MatchAccountsAsync"/>, nur dass hier angelegt wird, wo nichts passt.
    ///
    /// Die Zuordnungsregel steht bewusst NICHT noch einmal hier: eine Vorschau, die nach einer
    /// anderen Regel zuordnet als das Festschreiben, zeigt etwas anderes, als danach passiert - und
    /// das waere schlimmer als gar keine Vorschau.
    /// </summary>
    private async Task<AccountResolution> ResolveAccountsAsync(Guid userId, Guid fullWorthSpaceId, IReadOnlyList<FinanzguruRow> rows, CancellationToken ct)
    {
        var matches = await MatchAccountsAsync(userId, fullWorthSpaceId, rows, ct);
        var sourceGroups = rows.GroupBy(AccountKey, StringComparer.Ordinal).ToList();

        // Die Zuordnung liest ohne Nachverfolgung - zum Schreiben braucht es die verfolgten Entitaeten,
        // sonst bliebe das UpdatedAt unten wirkungslos.
        var matchedIds = matches.Values
            .Where(match => match.Account is not null)
            .Select(match => match.Account!.Id)
            .Distinct()
            .ToArray();
        var tracked = matchedIds.Length == 0
            ? []
            : await db.Accounts
                .Where(account => matchedIds.Contains(account.Id))
                .Include(account => account.Owners)
                .ToDictionaryAsync(account => account.Id, ct);

        var bySourceKey = new Dictionary<string, ResolvedAccount>(StringComparer.Ordinal);
        var matchedCount = 0;
        var created = 0;
        var createdAccountIds = new List<Guid>();
        var now = DateTimeOffset.UtcNow;

        foreach (var group in sourceGroups)
        {
            var sourceKey = group.Key;
            var sample = group.First();
            var match = matches[sourceKey];

            if (match.Account is { } found && tracked.TryGetValue(found.Id, out var account))
            {
                if (!match.MatchedLiveAccount)
                {
                    if (!account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
                        throw new FinanzguruImportConflictException("A matching Finanzguru import account already exists in this FullWorth Space but is owned by another user.");
                    // Ein zweiter Import derselben Quelle fasst das Konto nicht mehr an. Hier stand die
                    // Gegenprobe "hat es einen Kontostand?" und, wenn nicht, ein Zurueckstufen auf
                    // archiviert und ausserhalb des Vermoegens - was jeden Re-Import zum Ruecknehmer
                    // einer Nutzerentscheidung machte. Ein Importkonto ist jetzt von Anfang an ein
                    // richtiges.
                    account.UpdatedAt = now;
                }
                bySourceKey[sourceKey] = new(account, match.MatchedLiveAccount);
                matchedCount++;
                continue;
            }

            var hash = IdentificationHash(sourceKey);
            var normalizedReference = NormalizeAccountReference(sample.ReferenceAccount);
            var ibanLast4 = LooksLikeIban(normalizedReference) ? normalizedReference[^4..] : null;

            // Ein vollwertiges Konto, nicht mehr der stille Behaelter von frueher: der Store setzt
            // Eigentuemer und Standardgruppe und laesst IsActive/IncludeInNetWorth auf ihren
            // Vorgabewerten - das Konto ist ab dem Import sichtbar und zaehlt mit.
            var fresh = await accounts.CreateForImportAsync(userId, new ImportAccountWrite(
                fullWorthSpaceId,
                Provider,
                hash,
                $"finanzguru:{hash[..24]}",
                "Finanzguru Import",
                string.IsNullOrWhiteSpace(sample.ReferenceAccountName) ? "Finanzguru Konto" : sample.ReferenceAccountName.Trim(),
                "Imported history",
                sample.Currency,
                ibanLast4), ct);
            bySourceKey[sourceKey] = new(fresh, false);
            createdAccountIds.Add(fresh.Id);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return new AccountResolution(bySourceKey, matchedCount, created, createdAccountIds);
    }

    /// <summary>
    /// Auch die Vorschau prueft das, und zwar mit genau dieser Fassung (#131): eine Datei mit einer
    /// Aufteilung ohne Elternzeile ist als Ganzes unbrauchbar, und ein Zwischenstand dafuer waere
    /// einer, den niemand zu Ende bringen kann.
    /// </summary>
    internal static void ValidateSplits(IReadOnlyList<FinanzguruRow> rows)
    {
        var originals = rows.Where(row => row.SplitType == "Original")
            .ToDictionary(row => row.BookingId, StringComparer.Ordinal);
        var children = rows.Where(row => row.SplitType is "Teilbuchung" or "Restbetrag")
            .GroupBy(row => row.OriginalReferenceId!, StringComparer.Ordinal);

        foreach (var group in children)
        {
            if (!originals.TryGetValue(group.Key, out var original))
                throw new FinanzguruWorkbookException($"Split '{group.Key}' has no Original row in the export.");
            var childRows = group.ToList();
            if (childRows.Sum(row => row.Amount) != original.Amount)
                throw new FinanzguruWorkbookException($"Split '{group.Key}' does not add up to its original amount.");
            if (childRows.Any(row => AccountKey(row) != AccountKey(original)))
                throw new FinanzguruWorkbookException($"Split '{group.Key}' crosses account boundaries.");
        }

        foreach (var original in originals.Values)
        {
            if (!rows.Any(row => row.OriginalReferenceId == original.BookingId))
                throw new FinanzguruWorkbookException($"Split original '{original.BookingId}' has no child rows.");
        }
    }

    /// <summary>Dieselbe Gruppierung fuer die Vorschau - siehe <see cref="ExternalKeyOf"/>.</summary>
    internal static string AccountKeyOf(FinanzguruRow row) => AccountKey(row);

    private static string AccountKey(FinanzguruRow row)
    {
        var reference = NormalizeAccountReference(row.ReferenceAccount);
        if (!string.IsNullOrWhiteSpace(reference)) return reference;
        var name = row.ReferenceAccountName?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return $"NAME:{name.ToUpperInvariant()}:{row.Currency}";
        throw new FinanzguruWorkbookException($"Row {row.RowNumber}: Referenzkonto and Name Referenzkonto are both empty.");
    }

    private static string NormalizeAccountReference(string? value) =>
        string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    private static bool LooksLikeIban(string value) =>
        value.Length is >= 15 and <= 34
        && value.Length >= 2
        && char.IsLetter(value[0]) && char.IsLetter(value[1])
        && value.All(char.IsLetterOrDigit);

    private static string IdentificationHash(string sourceKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"finanzguru|{sourceKey}"))).ToLowerInvariant();

    private static string ExternalKey(string bookingId) => ExternalKeyOf(bookingId);

    /// <summary>
    /// Der Schluessel, unter dem eine Finanzguru-Buchung wiedererkannt wird. Die Vorschau muss ihn
    /// genauso bilden wie das Festschreiben - deshalb bildet sie ihn nicht selbst.
    /// </summary>
    internal static string ExternalKeyOf(string bookingId) => $"finanzguru:{bookingId.Trim()}";

    private static TransactionSignature Signature(FinanzguruRow row) => new(
        row.BookingDate,
        row.Amount,
        row.Currency.ToUpperInvariant(),
        MerchantNormalization.Normalize(row.Counterparty) ?? NormalizeDescription(row.Description));

    private static TransactionSignature Signature(FinanceTransaction row) => new(
        row.BookingDate ?? row.ValueDate,
        row.Amount,
        row.Currency.ToUpperInvariant(),
        row.NormalizedCounterparty ?? NormalizeDescription(row.Description));

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    internal sealed record TransactionSignature(DateOnly? Date, decimal Amount, string Currency, string? Party);
    private sealed record ResolvedAccount(FinanceAccount Account, bool MatchedLiveAccount);
    private sealed record AccountResolution(
        Dictionary<string, ResolvedAccount> BySourceKey, int Matched, int Created, IReadOnlyList<Guid> CreatedAccountIds);

    private sealed class CategoryResolver(FullWorthDbContext db, Guid fullWorthSpaceId, bool canCreate, List<FinanceCategory> categories)
    {
        private readonly Dictionary<string, Guid?> cache = new(StringComparer.OrdinalIgnoreCase);
        public int Created { get; private set; }
        public int Matched { get; private set; }
        public int Unmapped { get; private set; }

        public static async Task<CategoryResolver> CreateAsync(FullWorthDbContext db, Guid fullWorthSpaceId, bool canCreate, CancellationToken ct) =>
            new(db, fullWorthSpaceId, canCreate, await db.Categories.Where(category => category.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct));

        public Guid? Resolve(string? main, string? sub)
        {
            main = Normalize(main);
            sub = Normalize(sub);
            var cacheKey = $"{main}\u001f{sub}";
            if (cache.TryGetValue(cacheKey, out var cached)) return cached;

            if (main is null && sub is null)
            {
                cache[cacheKey] = null;
                return null;
            }

            Guid? parentId = null;
            if (main is not null)
            {
                parentId = ResolveSingle(main, null, main);
                if (!parentId.HasValue)
                {
                    cache[cacheKey] = null;
                    Unmapped++;
                    return null;
                }
            }

            var result = sub is null ? parentId : ResolveSingle(sub, parentId, $"{main}>{sub}");
            if (!result.HasValue && sub is not null) Unmapped++;
            cache[cacheKey] = result;
            return result;
        }

        private Guid? ResolveSingle(string name, Guid? parentId, string hierarchy)
        {
            var existing = categories.FirstOrDefault(category =>
                category.ParentId == parentId && string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Matched++;
                return existing.Id;
            }
            if (!canCreate) return null;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullWorthSpaceId:N}|{hierarchy.ToUpperInvariant()}"))).ToLowerInvariant();
            var entity = new FinanceCategory
            {
                FullWorthSpaceId = fullWorthSpaceId,
                Key = $"finanzguru-{hash[..24]}",
                Name = name,
                ParentId = parentId,
                IsSystem = false,
                IsArchived = false,
                SortOrder = 0,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Categories.Add(entity);
            categories.Add(entity);
            Created++;
            return entity.Id;
        }

        private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
