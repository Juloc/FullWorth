using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

/// <summary>Eine eingelesene Zeile, bevor daraus eine Buchung wird.</summary>
public sealed record Candidate(
    Guid Id, string? SourceAccount, DateOnly? Date, decimal Amount, string Currency, string? Counterparty,
    string? Description, string? Category, string? ExternalKey, string Fingerprint, string Status, string? Error);

/// <summary>Der Kontostand, den eine Kontoauszugsdatei nennt.</summary>
public sealed record JobStatementBalance(decimal Amount, string Currency, DateOnly AsOf);

/// <summary>Was ein Import hinterlassen hat.</summary>
public sealed record ImportJobCommitOutcome(int Imported, int Duplicates, bool BalanceApplied, string? BalanceSkipReason);

/// <summary>
/// Der Importauftrag mit seinen Zeilen: hochladen, ansehen, festschreiben, zuruecknehmen.
///
/// Jeder Schritt laeuft in einer Transaktion. Beim Festschreiben umfasst sie die Buchungen, die
/// Herkunftsverknuepfung, den Kontostand und den Abschluss des Auftrags - ohne die Verknuepfung
/// bliebe kein Nachweis, WELCHE Buchungen dieser Import erzeugt hat, und genau das machte das
/// Zuruecknehmen frueher unmoeglich.
/// </summary>
public sealed class ImportJobStore(FullWorthDbContext db, AuditService audit, FieldCipher cipher, AccountStore accounts)
{
    private const string JobColumns =
        "\"Id\",\"FileName\",\"AdapterKey\",\"Status\",\"SourceRowCount\",\"ReadyCount\",\"DuplicateCount\",\"ImportedCount\",\"ErrorCount\",\"CreatedAt\",\"CompletedAt\",\"RolledBackAt\",(SELECT count(*) FROM \"ImportTransactionLinks\" l WHERE l.\"ImportJobId\"=j.\"Id\")::int AS \"LinkCount\",(SELECT count(*) FROM \"ImportTransactionEnrichments\" e WHERE e.\"ImportJobId\"=j.\"Id\")::int AS \"EnrichmentCount\"";

    public async Task<string> BaseCurrencyAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var currency = await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Select(space => space.BaseCurrency)
            .SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(currency) ? "EUR" : currency;
    }

    public async Task<bool> OwnsJobAsync(Guid jobId, Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT 1 FROM \"ImportJobs\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"UserId\"=@uid",
            ("@id", jobId), ("@space", fullWorthSpaceId), ("@uid", userId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<List<object>> ListJobsAsync(Guid fullWorthSpaceId, Guid userId, CancellationToken ct, string? adapterKey = null)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        // Ohne den Filter zieht die Finanzguru-Seite jeden Importauftrag des Bereichs mit, auch CSV-
        // und Kontoauszugsimporte, die dort nichts verloren haben.
        var filter = string.IsNullOrWhiteSpace(adapterKey) ? "" : " AND \"AdapterKey\"=@adapter";
        await using var cmd = RawSql.Command(connection,
            $"SELECT {JobColumns} FROM \"ImportJobs\" j WHERE \"FullWorthSpaceId\"=@space AND \"UserId\"=@uid{filter} ORDER BY \"CreatedAt\" DESC",
            ("@space", fullWorthSpaceId), ("@uid", userId), ("@adapter", adapterKey));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<object>();
        while (await reader.ReadAsync(ct)) rows.Add(JobRow(reader));
        return rows;
    }

    public async Task<object?> FindJobAsync(Guid jobId, Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            $"SELECT {JobColumns} FROM \"ImportJobs\" j WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"UserId\"=@uid",
            ("@id", jobId), ("@space", fullWorthSpaceId), ("@uid", userId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? JobRow(reader) : null;
    }

    public async Task<List<object>> ListCandidateViewsAsync(Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"Id\",\"SourceAccount\",\"BookingDate\",\"Amount\",\"Currency\",\"Counterparty\",\"Description\",\"CategoryText\",\"ExternalKey\",\"DuplicateStatus\",\"ValidationStatus\",\"ValidationError\" FROM \"ImportCandidates\" WHERE \"ImportJobId\"=@job ORDER BY \"BookingDate\",\"Id\"",
            ("@job", jobId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
            rows.Add(new
            {
                id = RawSql.Guid(reader, "Id"),
                sourceAccount = RawSql.NullableString(reader, "SourceAccount"),
                bookingDate = RawSql.NullableDate(reader, "BookingDate"),
                amount = RawSql.Decimal(reader, "Amount"),
                currency = RawSql.String(reader, "Currency"),
                counterparty = RawSql.NullableString(reader, "Counterparty"),
                description = RawSql.NullableString(reader, "Description"),
                categoryText = RawSql.NullableString(reader, "CategoryText"),
                externalKey = RawSql.NullableString(reader, "ExternalKey"),
                duplicateStatus = RawSql.String(reader, "DuplicateStatus"),
                validationStatus = RawSql.String(reader, "ValidationStatus"),
                validationError = RawSql.NullableString(reader, "ValidationError")
            });
        return rows;
    }

    public async Task<List<Candidate>> ReadCandidatesAsync(Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"Id\",\"SourceAccount\",\"BookingDate\",\"Amount\",\"Currency\",\"Counterparty\",\"Description\",\"CategoryText\",\"ExternalKey\",\"RowFingerprint\",\"ValidationStatus\",\"ValidationError\" FROM \"ImportCandidates\" WHERE \"ImportJobId\"=@job ORDER BY \"BookingDate\",\"Id\"",
            ("@job", jobId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<Candidate>();
        while (await reader.ReadAsync(ct))
            rows.Add(new Candidate(
                RawSql.Guid(reader, "Id"), RawSql.NullableString(reader, "SourceAccount"),
                RawSql.NullableDate(reader, "BookingDate"), RawSql.Decimal(reader, "Amount"),
                RawSql.String(reader, "Currency"), RawSql.NullableString(reader, "Counterparty"),
                RawSql.NullableString(reader, "Description"), RawSql.NullableString(reader, "CategoryText"),
                RawSql.NullableString(reader, "ExternalKey"), RawSql.String(reader, "RowFingerprint"),
                RawSql.String(reader, "ValidationStatus"), RawSql.NullableString(reader, "ValidationError")));
        return rows;
    }

    /// <summary>Legt Auftrag und Zeilen an - fuer eine Tabellendatei ohne Kontostand.</summary>
    public Task CreateTableJobAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, string fileName, string sha, string adapterKey,
        IReadOnlyList<Candidate> candidates, int errorCount, CancellationToken ct) =>
        CreateJobAsync(userId, fullWorthSpaceId, jobId, fileName, sha, adapterKey,
            errorCount == candidates.Count ? "failed" : "ready",
            candidates, candidates.Count - errorCount, errorCount, null, null, ct);

    /// <summary>
    /// Legt Auftrag und Zeilen eines Kontoauszugs an. Ein Auszug mit Saldo aber ohne Buchungen ist
    /// eine gueltige Datei: er verankert das Konto.
    /// </summary>
    public Task CreateStatementJobAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, string fileName, string sha, string adapterKey,
        IReadOnlyList<Candidate> candidates, JobStatementBalance? balance, string? accountIdentifier,
        CancellationToken ct) =>
        CreateJobAsync(userId, fullWorthSpaceId, jobId, fileName, sha, adapterKey,
            candidates.Count == 0 && balance is null ? "failed" : "ready",
            candidates, candidates.Count, 0, balance, accountIdentifier, ct);

    private async Task CreateJobAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, string fileName, string sha, string adapterKey,
        string status, IReadOnlyList<Candidate> candidates, int readyCount, int errorCount,
        JobStatementBalance? balance, string? accountIdentifier, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = await RawSql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await using (var job = RawSql.Command(connection, """
INSERT INTO "ImportJobs" ("Id","FullWorthSpaceId","UserId","FileName","FileSha256","AdapterKey","Status","SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount","CreatedAt","UpdatedAt","StatementBalance","StatementBalanceCurrency","StatementBalanceDate","StatementAccount")
VALUES (@id,@space,@uid,@name,@sha,@adapter,@status,@source,@ready,0,0,@errors,@now,@now,@balance,@balanceCurrency,@balanceDate,@account)
""", ("@id", jobId), ("@space", fullWorthSpaceId), ("@uid", userId), ("@name", Path.GetFileName(fileName)),
            ("@sha", sha), ("@adapter", adapterKey), ("@status", status),
            ("@source", candidates.Count), ("@ready", readyCount), ("@errors", errorCount), ("@now", now),
            ("@balance", balance?.Amount), ("@balanceCurrency", balance?.Currency),
            ("@balanceDate", balance?.AsOf), ("@account", accountIdentifier)))
            await job.ExecuteNonQueryAsync(ct);

        foreach (var candidate in candidates)
        {
            await using var cmd = RawSql.Command(connection, """
INSERT INTO "ImportCandidates" ("Id","ImportJobId","SourceAccount","BookingDate","Amount","Currency","Counterparty","Description","CategoryText","ExternalKey","RowFingerprint","DuplicateStatus","ValidationStatus","ValidationError")
VALUES (@id,@job,@account,@date,@amount,@currency,@party,@description,@category,@external,@fingerprint,'new',@status,@error)
""", ("@id", candidate.Id), ("@job", jobId), ("@account", candidate.SourceAccount), ("@date", candidate.Date),
                ("@amount", candidate.Amount), ("@currency", candidate.Currency), ("@party", candidate.Counterparty),
                ("@description", candidate.Description), ("@category", candidate.Category),
                ("@external", candidate.ExternalKey), ("@fingerprint", candidate.Fingerprint),
                ("@status", candidate.Status), ("@error", candidate.Error));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        audit.Record(fullWorthSpaceId, userId, "import.uploaded", "ImportJob", jobId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public Task<FinanceAccount> AccountAsync(Guid accountId, CancellationToken ct) =>
        db.Accounts.SingleAsync(account => account.Id == accountId, ct);

    /// <summary>
    /// Schreibt die ausgewaehlten Zeilen als Buchungen fest.
    ///
    /// Die Dublettenpruefung laeuft gegen ZWEI Schluessel: den Importschluessel, der dieselbe Datei
    /// zweimal erkennt, und die Kombination aus Datum, Betrag, Waehrung und Gegenpart, die dieselbe
    /// Buchung aus einer ANDEREN Quelle erkennt. Beide Mengen werden einmal geladen statt je Zeile
    /// abgefragt.
    /// </summary>
    /// <summary>
    /// Legt das Zielkonto an, auf das dieser Import schreiben soll. Bewusst hier und nicht im
    /// Endpunkt: es entsteht in derselben Transaktion wie die Buchungen, bricht der Commit also ab,
    /// bleibt kein leeres Konto zurueck.
    /// </summary>
    private Task<FinanceAccount> CreateTargetAsync(
        Guid userId, Guid fullWorthSpaceId, string name, string currency, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N");
        return accounts.CreateForImportAsync(userId, new ImportAccountWrite(
            fullWorthSpaceId,
            "import",
            $"import:{key}",
            $"import:{key}",
            "Import",
            name,
            null,
            currency,
            null), ct);
    }

    public async Task<ImportJobCommitOutcome> CommitAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, FinanceAccount? account, string? newAccountName,
        IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        // Ein neues Konto hat noch nichts, wogegen sich vergleichen liesse - die Doppelpruefung unten
        // laeuft dann gegen leere Mengen.
        var existing = account is null ? [] : await db.Transactions.AsNoTracking()
            .Where(transaction => transaction.AccountId == account.Id)
            .Select(transaction => new
            {
                transaction.ExternalKey,
                Date = transaction.BookingDate ?? transaction.ValueDate,
                transaction.Amount,
                transaction.Currency,
                transaction.NormalizedCounterparty
            })
            .ToListAsync(ct);
        var existingKeys = existing.Where(row => !string.IsNullOrEmpty(row.ExternalKey))
            .Select(row => row.ExternalKey).ToHashSet(StringComparer.Ordinal);
        var existingSemantic = existing.Where(row => row.Date.HasValue)
            .Select(row => (row.Date!.Value, row.Amount, row.Currency, row.NormalizedCounterparty))
            .ToHashSet();

        var imported = 0;
        var duplicates = 0;
        var created = new List<FinanceTransaction>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Die Waehrung kommt aus der Datei selbst - sie ist das Einzige, was ueber das neue Konto
        // wirklich bekannt ist. Enthaelt sie keine, gilt die Basiswaehrung des Bereichs.
        if (account is null)
        {
            account = await CreateTargetAsync(
                userId, fullWorthSpaceId, newAccountName!,
                candidates.Select(row => row.Currency).FirstOrDefault(currency => !string.IsNullOrWhiteSpace(currency))
                    ?? await BaseCurrencyAsync(fullWorthSpaceId, ct),
                ct);
            await db.SaveChangesAsync(ct);
            // Der Auftrag merkt sich, dass ER dieses Konto angelegt hat - sonst bliebe es nach einer
            // Ruecknahme leer stehen, obwohl der Nutzer genau das ungeschehen machen wollte.
            var markConnection = await RawSql.OpenAsync(db, ct);
            await using var mark = RawSql.Command(markConnection,
                "UPDATE \"ImportJobs\" SET \"CreatedAccountId\"=@account WHERE \"Id\"=@job",
                ("@account", (object?)account.Id), ("@job", (object?)jobId));
            await mark.ExecuteNonQueryAsync(ct);
        }

        foreach (var candidate in candidates)
        {
            var normalized = MerchantNormalization.Normalize(candidate.Counterparty);
            if (existingSemantic.Contains((candidate.Date!.Value, candidate.Amount, candidate.Currency, normalized)))
            {
                duplicates++;
                await MarkCandidateAsync(candidate.Id, "duplicate", ct);
                continue;
            }

            var external = !string.IsNullOrWhiteSpace(candidate.ExternalKey)
                ? $"import:{jobId:N}:{candidate.ExternalKey}"
                : $"import:{jobId:N}:{candidate.Fingerprint}";
            if (!existingKeys.Add(external))
            {
                duplicates++;
                await MarkCandidateAsync(candidate.Id, "duplicate", ct);
                continue;
            }

            // Die Felder, die jede importierte Buchung traegt, stehen seit #131 an einer Stelle.
            // Dieser Weg setzt danach nichts weiter: eine Kategorie kennt er nicht.
            var entity = ImportCommitWrites.NewTransaction(
                account.Id, external, candidate.Date, candidate.Amount, candidate.Currency,
                candidate.Counterparty, normalized, candidate.Description,
                cipher.Protect("{\"source\":\"generic-import\"}") ?? "{}");
            db.Transactions.Add(entity);
            created.Add(entity);
            imported++;
            await MarkCandidateAsync(candidate.Id, "imported", ct);
        }
        await db.SaveChangesAsync(ct);

        var fileName = await JobFileNameAsync(jobId, ct);
        var (balanceApplied, balanceSkipped) = await ApplyStatementBalanceAsync(jobId, account, fileName, ct);

        await ImportTransactionProvenance.LinkAsync(db, jobId, created.Select(entity => entity.Id).ToArray(), ct);

        await ImportCommitWrites.CompleteJobAsync(db, jobId, imported, duplicates, ct);

        audit.Record(fullWorthSpaceId, userId, "import.committed", "ImportJob", jobId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ImportJobCommitOutcome(imported, duplicates, balanceApplied, balanceSkipped);
    }

    public async Task CancelAsync(Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "UPDATE \"ImportJobs\" SET \"Status\"='cancelled',\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"Status\" NOT IN ('completed','cancelled')",
            ("@now", DateTimeOffset.UtcNow), ("@id", jobId));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "import.cancelled", "ImportJob", jobId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(string Status, DateTimeOffset? RolledBackAt)?> JobStateAsync(Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"Status\",\"RolledBackAt\" FROM \"ImportJobs\" WHERE \"Id\"=@id", ("@id", jobId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (RawSql.String(reader, "Status"), RawSql.NullableTimestamp(reader, "RolledBackAt"));
    }

    /// <summary>
    /// Nimmt einen Import zurueck. Buchungen, an denen der Benutzer seither gearbeitet hat, bleiben
    /// stehen - was als "gearbeitet" zaehlt, steht im SQL von ImportTransactionProvenance.
    /// </summary>
    public async Task<int> RollbackAsync(Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = await RawSql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        int removed;
        await using (var delete = RawSql.Command(connection,
            ImportTransactionProvenance.DeleteImportedTransactionsSql, ("@job", jobId), ("@space", fullWorthSpaceId)))
            removed = await delete.ExecuteNonQueryAsync(ct);

        // Was dieser Import an FREMDEN Buchungen ergaenzt hat, geht auch zurueck - die Buchungen
        // selbst bleiben stehen, sie gehoeren ihm nicht (#131, Abschnitt 6/7). Nach dem Loeschen
        // oben, weil eine Buchung, die dieser Import selbst erzeugt hat, gar nicht mehr da ist und
        // ihre Notiz mit ihr verschwunden ist.
        await ImportTransactionEnrichment.RevertAsync(db, jobId, ct);

        // Hat der Import sein Zielkonto selbst angelegt und steht danach nichts mehr darin, geht es
        // mit: der Nutzer nimmt den Import zurueck, um ihn ungeschehen zu machen, und ein leeres
        // Konto, das er nie bestellt hat, waere das Gegenteil davon. Ein Konto mit Buchungen oder
        // einem Kontostand bleibt - dann hat er es inzwischen selbst benutzt.
        // Der Kontostand aus der Auszugsdatei gehoert dem Import und geht mit. Ein von Hand
        // erfasster bleibt - er ist die Arbeit des Nutzers und haelt das Konto am Leben.
        // Ein Konto ist Ergebnis DIESES Imports entweder ueber die alte Einzelspalte (CSV-/
        // Auszugsimport, hoechstens ein Konto) oder ueber "ImportJobCreatedAccounts" (Finanzguru, das
        // je Quellkonto in der Datei eines anlegen kann). Beide Wege zaehlen gleich.
        await using (var importedBalances = RawSql.Command(connection, """
DELETE FROM "BalanceSnapshots" b
USING "ImportJobs" j
WHERE j."Id"=@job AND b."Source"='import'
  AND (j."CreatedAccountId"=b."AccountId"
       OR EXISTS (SELECT 1 FROM "ImportJobCreatedAccounts" c WHERE c."ImportJobId"=j."Id" AND c."AccountId"=b."AccountId"))
""", ("@job", jobId)))
            await importedBalances.ExecuteNonQueryAsync(ct);

        await using (var emptyAccount = RawSql.Command(connection, """
DELETE FROM "Accounts" a
USING "ImportJobs" j
WHERE j."Id"=@job
  AND (j."CreatedAccountId"=a."Id"
       OR EXISTS (SELECT 1 FROM "ImportJobCreatedAccounts" c WHERE c."ImportJobId"=j."Id" AND c."AccountId"=a."Id"))
  AND NOT EXISTS (SELECT 1 FROM "Transactions" t WHERE t."AccountId"=a."Id")
  AND NOT EXISTS (SELECT 1 FROM "BalanceSnapshots" b WHERE b."AccountId"=a."Id")
""", ("@job", jobId)))
            await emptyAccount.ExecuteNonQueryAsync(ct);

        await using (var candidates = RawSql.Command(connection,
            "UPDATE \"ImportCandidates\" SET \"DuplicateStatus\"='rolled_back' WHERE \"ImportJobId\"=@job AND \"DuplicateStatus\"='imported'",
            ("@job", jobId)))
            await candidates.ExecuteNonQueryAsync(ct);

        await using (var job = RawSql.Command(connection,
            "UPDATE \"ImportJobs\" SET \"Status\"='rolled_back',\"RolledBackAt\"=@now,\"UpdatedAt\"=@now WHERE \"Id\"=@id",
            ("@id", jobId), ("@now", now)))
            await job.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "import.rolled_back", "ImportJob", jobId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return removed;
    }

    /// <summary>Wie viele Buchungen dieser Import nachweislich erzeugt hat.</summary>
    public Task<int> LinkCountAsync(Guid jobId, CancellationToken ct) =>
        ImportTransactionProvenance.LinkCountAsync(db, jobId, ct);

    /// <summary>Wie viele fremde Buchungen dieser Import nachweislich ergaenzt hat (#131).</summary>
    public Task<int> EnrichmentCountAsync(Guid jobId, CancellationToken ct) =>
        ImportTransactionEnrichment.CountAsync(db, jobId, ct);

    private async Task<string> JobFileNameAsync(Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"FileName\" FROM \"ImportJobs\" WHERE \"Id\"=@id", ("@id", jobId));
        return await cmd.ExecuteScalarAsync(ct) as string ?? string.Empty;
    }

    private Task MarkCandidateAsync(Guid candidateId, string status, CancellationToken ct) =>
        ImportCommitWrites.MarkCandidateAsync(db, candidateId, status, ct);

    /// <summary>
    /// Traegt den Schlusssaldo ein, den ein MT940/CAMT-Auszug genannt hat, sofern er noch das
    /// juengste Wort zu diesem Konto ist.
    ///
    /// Nichts wird ueberschrieben und nichts geloescht: ein Saldo ist eine Momentaufnahme, und ihn
    /// nicht hinzuzufuegen laesst schlicht den bestehenden Anker stehen.
    /// </summary>
    private async Task<(bool Applied, string? Reason)> ApplyStatementBalanceAsync(
        Guid jobId, FinanceAccount account, string fileName, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        decimal amount;
        string currency;
        DateOnly asOf;
        await using (var read = RawSql.Command(connection,
            "SELECT \"StatementBalance\",\"StatementBalanceCurrency\",\"StatementBalanceDate\" FROM \"ImportJobs\" WHERE \"Id\"=@id",
            ("@id", jobId)))
        {
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return (false, null);
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)) return (false, null);
            amount = reader.GetDecimal(0);
            currency = reader.GetString(1);
            asOf = DateOnly.FromDateTime(reader.GetDateTime(2));
        }

        var existing = await db.BalanceSnapshots.AsNoTracking()
            .Where(balance => balance.AccountId == account.Id)
            .Select(balance => new { balance.Source, balance.Currency, balance.ReferenceDate, balance.CapturedAt })
            .ToListAsync(ct);

        var outcome = StatementBalanceAnchor.Decide(
            new StatementBalance(amount, currency, asOf),
            account.Currency,
            existing.Select(balance => new StatementBalanceAnchor.ExistingBalance(
                balance.Source,
                balance.Currency,
                // Eine Zeile aus der Zeit vor den Stichtagen hat nur ihren Erfassungszeitpunkt.
                balance.ReferenceDate ?? DateOnly.FromDateTime(balance.CapturedAt.UtcDateTime))));
        if (outcome != StatementBalanceOutcome.Apply) return (false, StatementBalanceAnchor.SkipReason(outcome));

        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = account.Id,
            Amount = amount,
            Currency = currency,
            BalanceType = "closingBooked",
            Source = BalanceSources.Import,
            Note = Path.GetFileName(fileName),
            ReferenceDate = asOf,
            CapturedAt = DateTimeOffset.UtcNow
        });

        // Ein Konto, das per Auszug aktuell gehalten wird, ist ein echtes Konto mit einem echten Wert -
        // genau wie eines, das von Hand verankert wurde. Es archiviert und ausserhalb der Summen zu
        // lassen war die ganze Beschwerde.
        if (account.BankConnectionId is null && !account.IncludeInNetWorth)
        {
            var tracked = await db.Accounts.SingleAsync(row => row.Id == account.Id, ct);
            tracked.IsActive = true;
            tracked.IncludeInNetWorth = true;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return (true, null);
    }

    private static object JobRow(DbDataReader reader) => new
    {
        id = RawSql.Guid(reader, "Id"),
        fileName = RawSql.String(reader, "FileName"),
        adapterKey = RawSql.String(reader, "AdapterKey"),
        status = RawSql.String(reader, "Status"),
        sourceRowCount = RawSql.Int(reader, "SourceRowCount"),
        readyCount = RawSql.Int(reader, "ReadyCount"),
        duplicateCount = RawSql.Int(reader, "DuplicateCount"),
        importedCount = RawSql.Int(reader, "ImportedCount"),
        errorCount = RawSql.Int(reader, "ErrorCount"),
        createdAt = RawSql.Timestamp(reader, "CreatedAt"),
        completedAt = RawSql.NullableTimestamp(reader, "CompletedAt"),
        rolledBackAt = RawSql.NullableTimestamp(reader, "RolledBackAt"),
        // Only offered when the job actually left a trace to undo - an import committed before
        // provenance existed has no links, so the button would promise something it cannot do.
        // A trace is either a booking it created or one it enriched (#131): a Finanzguru import whose
        // every row matched the bank creates nothing and still changed forty categories, and the
        // rollback endpoint accepts it for exactly that reason.
        rollbackAvailable = RawSql.String(reader, "Status") == "completed"
            && RawSql.NullableTimestamp(reader, "RolledBackAt") is null
            && (RawSql.Int(reader, "LinkCount") > 0 || RawSql.Int(reader, "EnrichmentCount") > 0)
    };
}
