using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

/// <summary>Was ein Quellkonto der Datei treffen wuerde.</summary>
/// <param name="Retargetable">
/// Ob das Ziel hier waehlbar ist (#131, Abschnitt 4): nur fuer eine Quelle ohne eigenes Importkonto.
/// Fuehrt ein Importkonto sie schon, liegen dort Buchungen, und dann ist die Verknuepfung mit ihrem
/// Abgleich der Weg - nicht ein Umleiten, das dieselben Buchungen ein zweites Mal ablegen wuerde.
/// </param>
public sealed record FinanzguruStagedAccount(
    string SourceKey, string DisplayName, Guid? AccountId, string? AccountName, string Status, int Rows,
    bool Retargetable, string Currency);

/// <summary>Ein gewaehltes Ziel, das nicht angenommen werden kann - mit dem Grund, den die Seite zeigt.</summary>
public sealed class FinanzguruTargetRejectedException(string message) : Exception(message);

/// <summary>Die Vorschau eines Finanzguru-Imports, bevor irgendetwas geschrieben wurde.</summary>
public sealed record FinanzguruStagePreview(
    Guid JobId,
    int SourceRows,
    int NewRows,
    int AlreadyImported,
    int MatchedExisting,
    /// <summary>
    /// Wie viele der Treffer die Quelle noch ergaenzen wuerde - Kategorie, Aufteilung oder
    /// Umbuchungskennzeichnung an einer Buchung, an der noch niemand etwas entschieden hat
    /// (#131, Abschnitt 6/7). Teilmenge von <paramref name="MatchedExisting"/>.
    /// </summary>
    int EnrichedExisting,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<FinanzguruStagedAccount> Accounts);

/// <summary>
/// Der Finanzguru-Import bekommt seinen Zwischenschritt (#131, Schritt 4).
///
/// Er war der letzte Dateiimport, bei dem Hochladen gleich Festschreiben hiess: keine Vorschau, keine
/// Zeilenauswahl. Wer eine Datei mit drei Jahren Historie hochlud, sah erst danach, was daraus
/// geworden war - und konnte es nur noch als Ganzes zurueckrollen.
///
/// Hier entsteht KEINE dritte Import-Maschine. Der Zwischenschritt benutzt dieselben Tabellen wie
/// jeder andere Import (<c>ImportJobs</c>, <c>ImportCandidates</c>), und das Festschreiben ruft
/// genau den Weg auf, den es vorher auch ging - nur mit den Zeilen, die der Nutzer stehen liess.
/// Deshalb kann die Vorschau auch nichts anderes behaupten als das Ergebnis: sie fragt fuer die
/// Kontozuordnung dieselbe Methode (<c>MatchAccountsAsync</c>) und fuer die Dublettenfrage dieselben
/// zwei Regeln - bekannter externer Schluessel, und bei einem verknuepften Live-Konto die fachliche
/// Signatur.
///
/// Die gelesenen Zeilen liegen bis zum Festschreiben verschluesselt am Auftrag. Die Datei ein zweites
/// Mal hochzuladen waere der bequemere Weg gewesen und der falsche: der Ablauf der Issue nennt
/// "Datei auswaehlen" genau einmal.
/// </summary>
public sealed class FinanzguruStagingService(
    FullWorthDbContext db,
    FinanzguruWorkbookReader reader,
    FinanzguruImportService import,
    ImportSourceAccountStore sourceAccounts,
    FieldCipher cipher)
{
    private const string AdapterKey = "finanzguru_xlsx";

    /// <summary>Liest die Datei, sagt was passieren wuerde, und schreibt nichts als den Auftrag.</summary>
    public async Task<FinanzguruStagePreview?> StageAsync(
        Guid userId, Guid fullWorthSpaceId, Stream workbook, string fileName, CancellationToken ct)
    {
        var isMember = await db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);
        if (!isMember) return null;

        using var buffer = new MemoryStream();
        await workbook.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var sha = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        buffer.Position = 0;
        var rows = reader.Read(buffer);

        // Dieselbe Pruefung wie beim Festschreiben, und zwar VOR dem Auftrag: eine Datei mit einer
        // Aufteilung ohne Elternzeile ist als Ganzes unbrauchbar, und ein Auftrag dafuer waere ein
        // Zwischenstand, den niemand zu Ende bringen kann.
        FinanzguruImportService.ValidateSplits(rows);

        var jobId = Guid.NewGuid();
        var (preview, candidates) = await PreviewAsync(userId, fullWorthSpaceId, jobId, rows, null, ct);
        await WriteJobAsync(userId, fullWorthSpaceId, jobId, fileName, sha, rows, candidates, ct);
        return preview;
    }

    /// <summary>
    /// Waehlt fuer Quellen ohne Konto ein Ziel und rechnet die Vorschau neu (#131, Abschnitt 4).
    ///
    /// Neu gerechnet, nicht nur vorgemerkt: gegen ein echtes Konto werden aus "neu" Treffer, aus
    /// Treffern Ergaenzungen, und die Zahlen, die der Nutzer gleich bestaetigt, waeren sonst die
    /// eines Imports, der so nicht stattfindet. Die Kandidatenzeilen werden deshalb neu geschrieben.
    /// </summary>
    public async Task<FinanzguruStagePreview?> RetargetAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId,
        IReadOnlyDictionary<string, Guid> targets, CancellationToken ct)
    {
        var job = await ReadStagedJobAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job is null) return null;
        var rows = JsonSerializer.Deserialize<List<FinanzguruRow>>(job.Payload, JsonSerializerOptions.Web) ?? [];

        var (preview, candidates) = await PreviewAsync(userId, fullWorthSpaceId, jobId, rows, targets, ct);
        await ReplaceCandidatesAsync(jobId, candidates, ct);
        return preview;
    }

    /// <summary>
    /// Was passieren wuerde - einmal gerechnet, fuer das Einlesen und fuer das Umwaehlen.
    /// Ein gewaehltes Ziel, das nicht angenommen werden kann, bricht mit dem Grund ab, statt still
    /// uebergangen zu werden: die Vorschau zeigte sonst "wird angelegt", und der Nutzer wuesste nicht,
    /// warum seine Wahl nicht galt.
    /// </summary>
    private async Task<(FinanzguruStagePreview Preview, List<StagedCandidate> Candidates)> PreviewAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, IReadOnlyList<FinanzguruRow> rows,
        IReadOnlyDictionary<string, Guid>? targets, CancellationToken ct)
    {
        var parentRows = rows.Where(row => row.SplitType is null or "Original").ToList();
        if (targets is { Count: > 0 }) await ValidateTargetsAsync(userId, fullWorthSpaceId, parentRows, targets, ct);
        var matches = await import.MatchAccountsAsync(userId, fullWorthSpaceId, parentRows, ct, targets);
        var wouldResolve = await import.CategoryPredictorAsync(userId, fullWorthSpaceId, ct);

        var candidates = new List<StagedCandidate>();
        var accountSummaries = new List<FinanzguruStagedAccount>();

        foreach (var group in parentRows.GroupBy(FinanzguruImportService.AccountKeyOf, StringComparer.Ordinal))
        {
            var match = matches[group.Key];
            var sample = group.First();
            var sourceRows = group.ToList();
            var statuses = await ClassifyAsync(match, sourceRows, wouldResolve, ct);

            for (var index = 0; index < sourceRows.Count; index++)
                candidates.Add(new StagedCandidate(
                    sourceRows[index], statuses[index].Status, group.Key, statuses[index].WouldEnrich));

            accountSummaries.Add(new FinanzguruStagedAccount(
                group.Key,
                string.IsNullOrWhiteSpace(sample.ReferenceAccountName) ? group.Key : sample.ReferenceAccountName.Trim(),
                match.Account?.Id,
                match.Account?.DisplayName,
                match.WouldBeCreated ? "new" : match.MatchedLiveAccount ? "linked" : "import",
                sourceRows.Count,
                match.Retargetable,
                sample.Currency));
        }

        var preview = new FinanzguruStagePreview(
            jobId,
            rows.Count,
            candidates.Count(candidate => candidate.Status == "new"),
            candidates.Count(candidate => candidate.Status == "duplicate"),
            candidates.Count(candidate => candidate.Status == "matched"),
            candidates.Count(candidate => candidate.WouldEnrich),
            parentRows.Count == 0 ? null : parentRows.Min(row => row.BookingDate),
            parentRows.Count == 0 ? null : parentRows.Max(row => row.BookingDate),
            accountSummaries);
        return (preview, candidates);
    }

    /// <summary>
    /// Die zwei Bedingungen, unter denen ein gewaehltes Ziel gilt.
    ///
    /// 1. Die Quelle hat kein eigenes Importkonto. Hat sie eines, liegen dort Buchungen, und ein
    ///    Umleiten legte dieselben Buchungen ein zweites Mal ab - fuer diesen Fall gibt es die
    ///    Verknuepfung, die vorher abgleicht.
    /// 2. Keine Buchung dieser Quelle liegt schon in einem ANDEREN Konto. Sonst stuende sie nach dem
    ///    Import zweimal im Vermoegen - einmal dort, einmal im gewaehlten Ziel.
    ///
    /// Ob das Ziel dem Nutzer gehoert und die Waehrung passt, prueft die Zuordnung selbst: ein Ziel,
    /// das sie nicht findet, bleibt dort einfach unbesetzt.
    /// </summary>
    private async Task ValidateTargetsAsync(
        Guid userId, Guid fullWorthSpaceId, IReadOnlyList<FinanzguruRow> parentRows,
        IReadOnlyDictionary<string, Guid> targets, CancellationToken ct)
    {
        var baseline = await import.MatchAccountsAsync(userId, fullWorthSpaceId, parentRows, ct);
        var otherAccounts = await db.Accounts.AsNoTracking()
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId)
            .Select(account => account.Id)
            .ToArrayAsync(ct);
        foreach (var (sourceKey, targetId) in targets)
        {
            if (!baseline.TryGetValue(sourceKey, out var match))
                throw new FinanzguruTargetRejectedException("This source account is not part of the file.");
            if (!match.Retargetable)
                throw new FinanzguruTargetRejectedException(
                    "This source already has an import account. Link that account instead - it compares the history first.");

            var keys = parentRows
                .Where(row => FinanzguruImportService.AccountKeyOf(row) == sourceKey)
                .Select(row => FinanzguruImportService.ExternalKeyOf(row.BookingId))
                .ToArray();
            // Ueber die Konten des Raums, nicht ueber die Schluessel allein: der einzige Index auf den
            // Buchungen ist (AccountId, ExternalKey), und ohne die fuehrende Spalte laese Postgres
            // hier bei jeder Zielwahl die ganze Tabelle.
            var elsewhere = await db.Transactions.AsNoTracking()
                .AnyAsync(item => otherAccounts.Contains(item.AccountId)
                                  && item.AccountId != targetId
                                  && keys.Contains(item.ExternalKey), ct);
            if (elsewhere)
                throw new FinanzguruTargetRejectedException(
                    "Bookings of this source were already imported into another account. Choosing a different target would count them twice.");
        }
    }

    /// <summary>
    /// Schreibt die Datei fest - ueber denselben Weg wie vor dem Zwischenschritt, nur ohne die
    /// Zeilen, die der Nutzer abgewaehlt hat.
    ///
    /// <paramref name="selectedCandidateIds"/> <c>null</c> heisst "nichts abgewaehlt". Waehlbar sind
    /// nur die neuen Zeilen: eine abzuwaehlen, die ohnehin keine Buchung erzeugt, waere eine
    /// Entscheidung ueber nichts.
    /// </summary>
    public async Task<FinanzguruImportResult?> CommitAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId,
        IReadOnlyList<Guid>? selectedCandidateIds, IReadOnlyDictionary<string, Guid>? targets, CancellationToken ct)
    {
        var job = await ReadStagedJobAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job is null) return null;

        var rows = JsonSerializer.Deserialize<List<FinanzguruRow>>(job.Payload, JsonSerializerOptions.Web) ?? [];
        // Dieselbe Pruefung wie beim Umwaehlen - ein Aufruf, der die Vorschau ueberspringt, bekommt
        // keine Abkuerzung um sie herum.
        if (targets is { Count: > 0 })
            await ValidateTargetsAsync(userId, fullWorthSpaceId,
                rows.Where(row => row.SplitType is null or "Original").ToList(), targets, ct);
        var selected = selectedCandidateIds?.ToHashSet();
        var dropped = await DeselectedBookingIdsAsync(jobId, selected, ct);

        // Weggelassen wird nur, was der Nutzer ABGEWAEHLT hat - nicht alles ausser dem Gewaehlten.
        // Eine Zeile, die die Vorschau als vorhanden oder als Treffer gemeldet hat, geht weiter mit:
        // das Festschreiben erkennt sie noch einmal selbst, zaehlt sie richtig und traegt an einem
        // Treffer nach, was die Quelle zusaetzlich weiss (#131, Abschnitt 6/7). Sie hier
        // herauszufiltern hiesse, dem Festschreiben einen anderen Sachverhalt vorzulegen als den,
        // ueber den die Vorschau berichtet hat.
        //
        // Die Aufteilungen folgen ihrer Elternzeile. Sie einzeln waehlbar zu machen hiesse, eine
        // Buchung zuzulassen, deren Teile nicht mehr zusammen ergeben, was auf dem Konto steht.
        var keep = rows
            .Where(row => row.SplitType is "Teilbuchung" or "Restbetrag"
                ? row.OriginalReferenceId is null || !dropped.Contains(row.OriginalReferenceId)
                : !dropped.Contains(row.BookingId))
            .ToList();

        var result = await import.ImportRowsAsync(userId, fullWorthSpaceId, keep, ct, job.Sha, jobId, targets);
        if (result is null) return null;
        await FinishJobAsync(jobId, result.AlreadyImported + result.MatchedExistingTransactions, ct);

        // Gemerkt wird erst jetzt, nach dem Festschreiben - wie beim Spalten-Import. Beim naechsten
        // Import derselben Quelle steht das Ziel dann schon da, ohne dass ein Importkonto dazwischen
        // entstehen muss (#131, Abschnitt 4).
        if (targets is { Count: > 0 })
            await sourceAccounts.RememberAsync(
                fullWorthSpaceId, targets.ToDictionary(entry => entry.Key, entry => (Guid?)entry.Value), ct);

        // Die Zeilenzahl der DATEI, nicht die der Auswahl: "312 Zeilen, 40 uebernommen" ist die
        // Auskunft, "40 von 40" waere eine andere und falsche.
        return result with { SourceRows = rows.Count };
    }

    /// <summary>
    /// Was aus dieser Zeile wuerde: <c>new</c>, <c>duplicate</c> (dieser Import war schon da) oder
    /// <c>matched</c> (die Bank hat dieselbe Buchung schon geliefert).
    ///
    /// Beides sind genau die zwei Regeln, nach denen das Festschreiben entscheidet - hier nur ohne
    /// zu schreiben.
    /// </summary>
    private async Task<List<(string Status, bool WouldEnrich)>> ClassifyAsync(
        FinanzguruImportService.MatchedAccount match, List<FinanzguruRow> sourceRows,
        Func<string?, string?, bool> wouldResolve, CancellationToken ct)
    {
        // Ein Konto, das es noch nicht gibt, hat nichts, wogegen sich vergleichen liesse.
        if (match.Account is null) return [.. sourceRows.Select(_ => ("new", false))];

        var accountId = match.Account.Id;
        var keys = sourceRows.Select(row => FinanzguruImportService.ExternalKeyOf(row.BookingId))
            .Distinct(StringComparer.Ordinal).ToArray();
        var existingKeys = await db.Transactions.AsNoTracking()
            .Where(item => item.AccountId == accountId && keys.Contains(item.ExternalKey))
            .Select(item => item.ExternalKey)
            .ToListAsync(ct);
        var known = existingKeys.ToHashSet(StringComparer.Ordinal);

        Dictionary<FinanzguruImportService.TransactionSignature, Queue<Guid>> semantic = [];
        // Welche der Treffer noch zu ergaenzen waeren. Dieselbe Frage wie beim Festschreiben, mit
        // derselben Antwort - MayEnrich steht einmal, nicht hier ein zweites Mal.
        var enrichable = new HashSet<Guid>();
        if (match.MatchedLiveAccount)
        {
            var minDate = sourceRows.Min(row => row.BookingDate);
            var maxDate = sourceRows.Max(row => row.BookingDate);
            var existing = await FinanzguruImportService.SemanticMatchPool(db.Transactions.AsNoTracking(), accountId, minDate, maxDate)
                .Select(item => new
                {
                    item.Id, item.BookingDate, item.Amount, item.Currency, item.NormalizedCounterparty,
                    item.CategoryId, item.CategorizationSource, item.IsTransfer,
                    HasAllocations = db.TransactionAllocations.Any(allocation => allocation.TransactionId == item.Id)
                })
                .ToListAsync(ct);
            semantic = existing
                .GroupBy(item => new FinanzguruImportService.TransactionSignature(
                    item.BookingDate, item.Amount, item.Currency, item.NormalizedCounterparty))
                .ToDictionary(group => group.Key, group => new Queue<Guid>(group.OrderBy(item => item.Id).Select(item => item.Id)));
            foreach (var item in existing)
            {
                if (ImportTransactionEnrichment.MayEnrich(
                        item.CategoryId, item.CategorizationSource, item.IsTransfer, item.HasAllocations))
                    enrichable.Add(item.Id);
            }
        }

        var splitParents = sourceRows
            .Where(row => row.SplitType is "Original")
            .Select(row => row.BookingId)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<(string Status, bool WouldEnrich)>(sourceRows.Count);
        foreach (var row in sourceRows)
        {
            if (known.Contains(FinanzguruImportService.ExternalKeyOf(row.BookingId))) { result.Add(("duplicate", false)); continue; }
            var signature = new FinanzguruImportService.TransactionSignature(
                row.BookingDate, row.Amount, row.Currency, MerchantNormalization.Normalize(row.Counterparty));
            if (match.MatchedLiveAccount && semantic.TryGetValue(signature, out var queue) && queue.Count > 0)
            {
                var existingId = queue.Dequeue();
                // Beizutragen gibt es etwas, wenn die Quelle eine Aufteilung, die
                // Umbuchungskennzeichnung oder eine Kategorie mitbringt, die das Festschreiben auch
                // tatsaechlich aufloesen wird - fuer wen den Raum nicht besitzt, ist eine unbekannte
                // Kategorie keine (siehe CategoryPredictorAsync).
                var brings = splitParents.Contains(row.BookingId)
                    || row.IsTransfer
                    || wouldResolve(row.MainCategory, row.SubCategory);
                result.Add(("matched", enrichable.Contains(existingId) && brings));
                continue;
            }
            result.Add(("new", false));
        }
        return result;
    }

    private sealed record StagedCandidate(FinanzguruRow Row, string Status, string SourceKey, bool WouldEnrich);
    private sealed record StagedJob(string Payload, string Sha);

    private async Task WriteJobAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, string fileName, string sha,
        IReadOnlyList<FinanzguruRow> rows, IReadOnlyList<StagedCandidate> candidates, CancellationToken ct)
    {
        var payload = cipher.Protect(JsonSerializer.Serialize(rows, JsonSerializerOptions.Web)) ?? "";
        var now = DateTimeOffset.UtcNow;
        var connection = await RawSql.OpenAsync(db, ct);

        await using (var command = RawSql.Command(connection, """
INSERT INTO "ImportJobs" ("Id","FullWorthSpaceId","UserId","FileName","FileSha256","AdapterKey","Status",
                          "SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount",
                          "CreatedAt","UpdatedAt","SourcePayloadEncrypted")
VALUES (@id,@space,@uid,@name,@sha,@adapter,'ready',@source,@ready,@duplicates,0,0,@now,@now,@payload)
""",
            ("@id", jobId), ("@space", fullWorthSpaceId), ("@uid", userId), ("@name", fileName), ("@sha", sha),
            ("@adapter", AdapterKey), ("@source", rows.Count),
            ("@ready", candidates.Count(candidate => candidate.Status == "new")),
            ("@duplicates", candidates.Count(candidate => candidate.Status != "new")),
            ("@now", now), ("@payload", payload)))
            await command.ExecuteNonQueryAsync(ct);

        await InsertCandidatesAsync(connection, null, jobId, candidates, ct);
    }

    /// <summary>
    /// Schreibt die Kandidaten in Stapeln zu <see cref="InsertBatch"/> Zeilen. Zeile fuer Zeile waren
    /// das bei einer Datei mit drei Jahren Historie ueber 700 Rundreisen - und seit die Zielwahl die
    /// Kandidaten neu schreibt, bei jeder Aenderung einer Auswahl noch einmal so viele.
    /// </summary>
    private static async Task InsertCandidatesAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction,
        Guid jobId, IReadOnlyList<StagedCandidate> candidates, CancellationToken ct)
    {
        foreach (var batch in candidates.Chunk(InsertBatch))
        {
            var values = new List<string>(batch.Length);
            var parameters = new List<(string, object?)> { ("@job", jobId) };
            for (var index = 0; index < batch.Length; index++)
            {
                var candidate = batch[index];
                values.Add($"(@id{index},@job,@account{index},@date{index},@amount{index},@currency{index},"
                    + $"@party{index},@description{index},@category{index},@key{index},@fingerprint{index},@status{index},'ready')");
                parameters.AddRange([
                    ($"@id{index}", Guid.NewGuid()), ($"@account{index}", candidate.SourceKey),
                    ($"@date{index}", candidate.Row.BookingDate), ($"@amount{index}", candidate.Row.Amount),
                    ($"@currency{index}", candidate.Row.Currency), ($"@party{index}", candidate.Row.Counterparty),
                    ($"@description{index}", candidate.Row.Description), ($"@category{index}", Category(candidate.Row)),
                    ($"@key{index}", FinanzguruImportService.ExternalKeyOf(candidate.Row.BookingId)),
                    // Der Fingerabdruck ist hier die Buchungskennung selbst: Finanzguru vergibt sie, und
                    // sie ist genau das, woran das Festschreiben die Zeile wiederfindet.
                    ($"@fingerprint{index}", candidate.Row.BookingId), ($"@status{index}", candidate.Status)
                ]);
            }
            await using var command = RawSql.Command(connection, """
INSERT INTO "ImportCandidates" ("Id","ImportJobId","SourceAccount","BookingDate","Amount","Currency",
                                "Counterparty","Description","CategoryText","ExternalKey","RowFingerprint",
                                "DuplicateStatus","ValidationStatus")
VALUES
""" + string.Join(",\n", values), [.. parameters]);
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    // Zwoelf Parameter je Zeile; Postgres erlaubt 65 535 je Anweisung. 200 Zeilen bleiben weit
    // darunter und halten die einzelne Anweisung klein genug, um sie im Log noch lesen zu koennen.
    private const int InsertBatch = 200;

    /// <summary>
    /// Schreibt die Kandidaten eines Auftrags neu. Die Kennungen wechseln dabei - eine Zeilenauswahl
    /// holt ihre Liste deshalb immer frisch, statt sich an alte zu erinnern.
    /// </summary>
    private async Task ReplaceCandidatesAsync(Guid jobId, IReadOnlyList<StagedCandidate> candidates, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var delete = RawSql.Command(connection,
            "DELETE FROM \"ImportCandidates\" WHERE \"ImportJobId\"=@job", ("@job", jobId)))
        {
            delete.Transaction = transaction;
            await delete.ExecuteNonQueryAsync(ct);
        }
        await InsertCandidatesAsync(connection, transaction, jobId, candidates, ct);
        await using (var counts = RawSql.Command(connection, """
UPDATE "ImportJobs" SET "ReadyCount"=@ready,"DuplicateCount"=@duplicates,"UpdatedAt"=@now WHERE "Id"=@id
""",
            ("@id", jobId), ("@ready", candidates.Count(candidate => candidate.Status == "new")),
            ("@duplicates", candidates.Count(candidate => candidate.Status != "new")),
            ("@now", DateTimeOffset.UtcNow)))
        {
            counts.Transaction = transaction;
            await counts.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static string? Category(FinanzguruRow row) =>
        string.IsNullOrWhiteSpace(row.SubCategory) ? row.MainCategory : $"{row.MainCategory} › {row.SubCategory}";

    private async Task<StagedJob?> ReadStagedJobAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "SourcePayloadEncrypted","FileSha256" FROM "ImportJobs"
WHERE "Id"=@id AND "FullWorthSpaceId"=@space AND "UserId"=@uid AND "AdapterKey"=@adapter AND "Status"='ready'
""",
            ("@id", jobId), ("@space", fullWorthSpaceId), ("@uid", userId), ("@adapter", AdapterKey));
        await using var value = await command.ExecuteReaderAsync(ct);
        if (!await value.ReadAsync(ct) || value.IsDBNull(0)) return null;
        var payload = cipher.Unprotect(value.GetString(0));
        return payload is null ? null : new StagedJob(payload, value.GetString(1));
    }

    /// <summary>
    /// Welche Buchungskennungen der Nutzer abgewaehlt hat. Nur neue Zeilen sind ueberhaupt
    /// waehlbar - eine Zeile abzuwaehlen, die ohnehin keine Buchung erzeugt, waere eine
    /// Entscheidung ueber nichts.
    /// </summary>
    private async Task<HashSet<string>> DeselectedBookingIdsAsync(
        Guid jobId, HashSet<Guid>? selected, CancellationToken ct)
    {
        if (selected is null) return [];
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"Id\",\"RowFingerprint\",\"DuplicateStatus\" FROM \"ImportCandidates\" WHERE \"ImportJobId\"=@job",
            ("@job", jobId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var dropped = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var bookingId = reader.GetString(1);
            var status = reader.GetString(2);
            if (status == "new" && !selected.Contains(id)) dropped.Add(bookingId);
        }
        return dropped;
    }

    /// <summary>
    /// Die gelesene Datei verlaesst den Auftrag, sobald sie nicht mehr gebraucht wird - ein
    /// abgeschlossener Import soll keine Kopie der hochgeladenen Datei mit sich herumtragen.
    ///
    /// Die Dublettenzahl wird hier nachgetragen, weil sie erst aus der Vorschau kommt:
    /// FinalizeImportJobCountsAsync haette nur die der geschriebenen Zeilen gekannt, also 0.
    /// </summary>
    private async Task FinishJobAsync(Guid jobId, int duplicates, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "UPDATE \"ImportJobs\" SET \"SourcePayloadEncrypted\"=NULL,\"DuplicateCount\"=@duplicates WHERE \"Id\"=@id",
            ("@id", jobId), ("@duplicates", duplicates));
        await command.ExecuteNonQueryAsync(ct);
    }
}
