using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Transactions;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Der gemeinsame Schreibteil der beiden Import-Maschinen (#131).
///
/// <c>/api/import-jobs</c> und <c>/api/import-mapping</c> schreiben in DIESELBEN Tabellen
/// (<c>ImportJobs</c>, <c>ImportCandidates</c>, <c>Transactions</c>), und dadurch gilt die
/// Ruecknahme ueber <c>ImportTransactionLinks</c> auch fuer beide. Was sie trotzdem doppelt hatten,
/// war der Weg dorthin: Zeile als erledigt markieren, Auftrag abschliessen, und die Felder, die eine
/// importierte Buchung immer traegt.
///
/// Verschieden bleiben darf, was wirklich verschieden IST: woher die Dublettenentscheidung kommt
/// (die eine rechnet sie beim Festschreiben, die andere bekommt sie aus der Vorschau), ob es eine
/// Kontozuordnung je Zeile gibt, und ob Kategorien aus der Datei uebernommen werden. Das sind
/// verschiedene Fragen und keine zwei Antworten auf dieselbe.
///
/// <see cref="ImportEngineAgreementTests"/> haelt fest, dass beide Wege bei derselben Datei dasselbe
/// schreiben - diese Datei ist der Schritt danach: dieselbe Datei, und jetzt auch derselbe Stift.
/// </summary>
internal static class ImportCommitWrites
{
    /// <summary>
    /// Die Felder, die JEDE importierte Buchung traegt - unabhaengig davon, welcher Weg sie anlegt.
    ///
    /// Was der Aufrufer danach noch setzt, ist das, was seinen Weg ausmacht: Kategorie und
    /// Kategoriequelle, wenn die Datei welche hatte.
    /// </summary>
    internal static FinanceTransaction NewTransaction(
        Guid accountId,
        string externalKey,
        DateOnly? date,
        decimal amount,
        string currency,
        string? counterparty,
        string? normalizedCounterparty,
        string? description,
        string rawJson)
    {
        var now = DateTimeOffset.UtcNow;
        return new FinanceTransaction
        {
            AccountId = accountId,
            ExternalKey = externalKey,
            Status = "BOOK",
            // Ein Dateiimport nennt EIN Datum. Es als Buchungs- UND Valutadatum zu fuehren ist keine
            // Annahme, sondern das Gegenteil: ein erfundenes zweites Datum waere eine.
            BookingDate = date,
            ValueDate = date,
            Amount = amount,
            Currency = currency,
            Counterparty = counterparty,
            NormalizedCounterparty = normalizedCounterparty,
            Description = description,
            CategorizationSource = "none",
            RawJson = rawJson,
            FirstSeenAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Wie es dieser Zeile ergangen ist: <c>imported</c> oder <c>duplicate</c>.</summary>
    internal static async Task MarkCandidateAsync(
        FullWorthDbContext db, Guid candidateId, string state, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "UPDATE \"ImportCandidates\" SET \"DuplicateStatus\"=@state WHERE \"Id\"=@id",
            ("@state", state), ("@id", candidateId));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Der Auftrag ist fertig. Die Zahlen gehoeren dazu: ohne sie sagt der Verlauf spaeter nur, DASS
    /// importiert wurde, nicht was dabei herauskam.
    /// </summary>
    internal static async Task CompleteJobAsync(
        FullWorthDbContext db, Guid jobId, int imported, int duplicates, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "UPDATE \"ImportJobs\" SET \"Status\"='completed',\"ImportedCount\"=@imported,"
            + "\"DuplicateCount\"=@duplicates,\"UpdatedAt\"=@now,\"CompletedAt\"=@now WHERE \"Id\"=@id",
            ("@imported", imported), ("@duplicates", duplicates),
            ("@now", DateTimeOffset.UtcNow), ("@id", jobId));
        await command.ExecuteNonQueryAsync(ct);
    }
}
