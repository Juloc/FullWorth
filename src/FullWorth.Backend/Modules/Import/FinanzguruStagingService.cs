using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

/// <summary>Was ein Quellkonto der Datei treffen wuerde.</summary>
public sealed record FinanzguruStagedAccount(
    string SourceKey, string DisplayName, Guid? AccountId, string? AccountName, string Status, int Rows);

/// <summary>Die Vorschau eines Finanzguru-Imports, bevor irgendetwas geschrieben wurde.</summary>
public sealed record FinanzguruStagePreview(
    Guid JobId,
    int SourceRows,
    int NewRows,
    int AlreadyImported,
    int MatchedExisting,
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

        var parentRows = rows.Where(row => row.SplitType is null or "Original").ToList();
        var matches = await import.MatchAccountsAsync(userId, fullWorthSpaceId, parentRows, ct);

        var jobId = Guid.NewGuid();
        var candidates = new List<StagedCandidate>();
        var accountSummaries = new List<FinanzguruStagedAccount>();

        foreach (var group in parentRows.GroupBy(FinanzguruImportService.AccountKeyOf, StringComparer.Ordinal))
        {
            var match = matches[group.Key];
            var sample = group.First();
            var sourceRows = group.ToList();
            var statuses = await ClassifyAsync(match, sourceRows, ct);

            for (var index = 0; index < sourceRows.Count; index++)
                candidates.Add(new StagedCandidate(sourceRows[index], statuses[index], group.Key));

            accountSummaries.Add(new FinanzguruStagedAccount(
                group.Key,
                string.IsNullOrWhiteSpace(sample.ReferenceAccountName) ? group.Key : sample.ReferenceAccountName.Trim(),
                match.Account?.Id,
                match.Account?.DisplayName,
                match.WouldBeCreated ? "new" : match.MatchedLiveAccount ? "linked" : "import",
                sourceRows.Count));
        }

        await WriteJobAsync(userId, fullWorthSpaceId, jobId, fileName, sha, rows, candidates, ct);

        return new FinanzguruStagePreview(
            jobId,
            rows.Count,
            candidates.Count(candidate => candidate.Status == "new"),
            candidates.Count(candidate => candidate.Status == "duplicate"),
            candidates.Count(candidate => candidate.Status == "matched"),
            parentRows.Count == 0 ? null : parentRows.Min(row => row.BookingDate),
            parentRows.Count == 0 ? null : parentRows.Max(row => row.BookingDate),
            accountSummaries);
    }

    /// <summary>
    /// Schreibt die gewaehlten Zeilen fest - ueber denselben Weg wie vor dem Zwischenschritt.
    ///
    /// <paramref name="selectedCandidateIds"/> leer heisst "alles, was neu ist". Eine Zeile, die die
    /// Vorschau schon als vorhanden gemeldet hat, kommt gar nicht erst mit: sie abzuwaehlen waere
    /// eine Entscheidung ueber etwas, das ohnehin nicht passiert.
    /// </summary>
    public async Task<FinanzguruImportResult?> CommitAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId,
        IReadOnlyList<Guid>? selectedCandidateIds, CancellationToken ct)
    {
        var job = await ReadStagedJobAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job is null) return null;

        var rows = JsonSerializer.Deserialize<List<FinanzguruRow>>(job.Payload, JsonSerializerOptions.Web) ?? [];
        var selected = selectedCandidateIds?.ToHashSet();
        var staged = await ReadCandidatesAsync(jobId, selected, ct);

        // Die Aufteilungen folgen ihrer Elternzeile. Sie einzeln waehlbar zu machen hiesse, eine
        // Buchung zuzulassen, deren Teile nicht mehr zusammen ergeben, was auf dem Konto steht.
        var keep = rows
            .Where(row => row.SplitType is "Teilbuchung" or "Restbetrag"
                ? row.OriginalReferenceId is not null && staged.Wanted.Contains(row.OriginalReferenceId)
                : staged.Wanted.Contains(row.BookingId))
            .ToList();

        var result = await import.ImportRowsAsync(userId, fullWorthSpaceId, keep, ct, job.Sha, jobId);
        if (result is null) return null;
        await FinishJobAsync(jobId, staged.Duplicate + staged.Matched, ct);

        // Die Zahlen der DATEI, nicht die der Auswahl. ImportRowsAsync bekommt nur die Zeilen, die es
        // schreiben soll, und wuesste von den uebrigen nichts - "312 Zeilen, 40 uebernommen, 260
        // bereits vorhanden" waere sonst zu "40 Zeilen, 40 uebernommen, 0 vorhanden" geworden, was
        // dasselbe Ereignis anders und falsch beschreibt.
        return result with
        {
            SourceRows = rows.Count,
            AlreadyImported = staged.Duplicate,
            MatchedExistingTransactions = staged.Matched
        };
    }

    /// <summary>
    /// Was aus dieser Zeile wuerde: <c>new</c>, <c>duplicate</c> (dieser Import war schon da) oder
    /// <c>matched</c> (die Bank hat dieselbe Buchung schon geliefert).
    ///
    /// Beides sind genau die zwei Regeln, nach denen das Festschreiben entscheidet - hier nur ohne
    /// zu schreiben.
    /// </summary>
    private async Task<List<string>> ClassifyAsync(
        FinanzguruImportService.MatchedAccount match, List<FinanzguruRow> sourceRows, CancellationToken ct)
    {
        // Ein Konto, das es noch nicht gibt, hat nichts, wogegen sich vergleichen liesse.
        if (match.Account is null) return [.. sourceRows.Select(_ => "new")];

        var accountId = match.Account.Id;
        var keys = sourceRows.Select(row => FinanzguruImportService.ExternalKeyOf(row.BookingId))
            .Distinct(StringComparer.Ordinal).ToArray();
        var existingKeys = await db.Transactions.AsNoTracking()
            .Where(item => item.AccountId == accountId && keys.Contains(item.ExternalKey))
            .Select(item => item.ExternalKey)
            .ToListAsync(ct);
        var known = existingKeys.ToHashSet(StringComparer.Ordinal);

        Dictionary<FinanzguruImportService.TransactionSignature, Queue<Guid>> semantic = [];
        if (match.MatchedLiveAccount)
        {
            var minDate = sourceRows.Min(row => row.BookingDate);
            var maxDate = sourceRows.Max(row => row.BookingDate);
            var existing = await db.Transactions.AsNoTracking()
                .Where(item => item.AccountId == accountId
                               && item.BookingDate >= minDate && item.BookingDate <= maxDate
                               && item.Status != "PDNG"
                               && !item.ExternalKey.StartsWith("finanzguru:"))
                .Select(item => new { item.Id, item.BookingDate, item.Amount, item.Currency, item.NormalizedCounterparty })
                .ToListAsync(ct);
            semantic = existing
                .GroupBy(item => new FinanzguruImportService.TransactionSignature(
                    item.BookingDate, item.Amount, item.Currency, item.NormalizedCounterparty))
                .ToDictionary(group => group.Key, group => new Queue<Guid>(group.OrderBy(item => item.Id).Select(item => item.Id)));
        }

        var result = new List<string>(sourceRows.Count);
        foreach (var row in sourceRows)
        {
            if (known.Contains(FinanzguruImportService.ExternalKeyOf(row.BookingId))) { result.Add("duplicate"); continue; }
            var signature = new FinanzguruImportService.TransactionSignature(
                row.BookingDate, row.Amount, row.Currency, MerchantNormalization.Normalize(row.Counterparty));
            if (match.MatchedLiveAccount && semantic.TryGetValue(signature, out var queue) && queue.Count > 0)
            {
                queue.Dequeue();
                result.Add("matched");
                continue;
            }
            result.Add("new");
        }
        return result;
    }

    private sealed record StagedCandidate(FinanzguruRow Row, string Status, string SourceKey);
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

        foreach (var candidate in candidates)
        {
            await using var command = RawSql.Command(connection, """
INSERT INTO "ImportCandidates" ("Id","ImportJobId","SourceAccount","BookingDate","Amount","Currency",
                                "Counterparty","Description","CategoryText","ExternalKey","RowFingerprint",
                                "DuplicateStatus","ValidationStatus")
VALUES (@id,@job,@account,@date,@amount,@currency,@party,@description,@category,@key,@fingerprint,@status,'ready')
""",
                ("@id", Guid.NewGuid()), ("@job", jobId), ("@account", candidate.SourceKey),
                ("@date", candidate.Row.BookingDate), ("@amount", candidate.Row.Amount),
                ("@currency", candidate.Row.Currency), ("@party", candidate.Row.Counterparty),
                ("@description", candidate.Row.Description),
                ("@category", Category(candidate.Row)),
                ("@key", FinanzguruImportService.ExternalKeyOf(candidate.Row.BookingId)),
                // Der Fingerabdruck ist hier die Buchungskennung selbst: Finanzguru vergibt sie, und
                // sie ist genau das, woran das Festschreiben die Zeile wiederfindet.
                ("@fingerprint", candidate.Row.BookingId), ("@status", candidate.Status));
            await command.ExecuteNonQueryAsync(ct);
        }
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
    /// Welche Buchungskennungen festgeschrieben werden sollen - und wie viele Zeilen aus einem
    /// anderen Grund als der Abwahl des Nutzers draussen bleiben.
    /// </summary>
    private async Task<(HashSet<string> Wanted, int Duplicate, int Matched)> ReadCandidatesAsync(
        Guid jobId, HashSet<Guid>? selected, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"Id\",\"RowFingerprint\",\"DuplicateStatus\" FROM \"ImportCandidates\" WHERE \"ImportJobId\"=@job",
            ("@job", jobId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = 0;
        var matched = 0;
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var bookingId = reader.GetString(1);
            var status = reader.GetString(2);
            if (status == "duplicate") { duplicate++; continue; }
            if (status == "matched") { matched++; continue; }
            if (selected is not null && !selected.Contains(id)) continue;
            wanted.Add(bookingId);
        }
        return (wanted, duplicate, matched);
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
