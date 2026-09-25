using FullWorth.Backend.Data;

namespace FullWorth.Backend.Modules.Import;

/// <summary>Was ein Import an einer fremden Buchung gesetzt hat.</summary>
/// <param name="TransactionId">Die vorhandene Buchung - nicht vom Import erzeugt.</param>
/// <param name="CategoryId">Die vergebene Kategorie, oder <c>null</c>, wenn nur Aufteilungen dazukamen.</param>
/// <param name="SetTransfer">Ob der Import die Umbuchungskennzeichnung gesetzt hat.</param>
internal readonly record struct ImportedEnrichment(Guid TransactionId, Guid? CategoryId, bool SetTransfer);

/// <summary>
/// Der zweite Fall aus #131, Abschnitt 6/7: dieselbe reale Buchung kommt aus zwei Quellen.
///
/// <see cref="ImportTransactionProvenance"/> beantwortet "wer hat diese Buchung ERZEUGT" - eine
/// Buchung, ein Auftrag, und beim Ruecknehmen verschwindet sie. Hier steht die andere Frage: wer hat
/// zu einer Buchung, die es schon gab, etwas BEIGETRAGEN. Die Bank liefert Datum, Betrag und
/// Gegenpartei; Finanzguru liefert dieselbe Buchung noch einmal, dazu aber eine Kategorie, ihre
/// Aufteilung und die Umbuchungskennzeichnung. Diese zweite Zeile wurde bisher gezaehlt und
/// weggeworfen.
///
/// Zwei Regeln halten das harmlos:
///
/// 1. Ergaenzt wird nur, wo nichts entschieden ist - keine Kategorie, keine Aufteilung, keine
///    Umbuchung, und die Kategorisierung stammt nicht vom Nutzer
///    (<c>CategorizationSource == "manual"</c>). Eine Buchung, an der jemand gearbeitet hat, bleibt
///    unberuehrt; es gibt also nichts zu ueberschreiben. Das ist absichtlich EIN Zustand und nicht
///    drei einzelne Pruefungen: "hat noch niemand angefasst" ist eine Aussage ueber die Buchung, und
///    eine halb ergaenzte waere schwerer zu erklaeren als eine gar nicht ergaenzte.
/// 2. Zurueckgenommen wird nur der eigene Beitrag, und nur solange er noch dasteht. Wer die Kategorie
///    seither selbst gesetzt hat, traegt <c>"manual"</c> und behaelt sie.
/// </summary>
internal static class ImportTransactionEnrichment
{
    /// <summary>
    /// Die Bedingung, unter der eine vorhandene Buchung ergaenzt werden darf. Steht hier und nicht
    /// beim Aufrufer, damit die Vorschau (<c>FinanzguruStagingService</c>) und das Festschreiben
    /// dieselbe Frage nicht zweimal verschieden beantworten.
    /// </summary>
    internal static bool MayEnrich(Guid? categoryId, string? categorizationSource, bool isTransfer, bool hasAllocations) =>
        categoryId is null && !hasAllocations && !isTransfer
        && !string.Equals(categorizationSource, "manual", StringComparison.Ordinal);

    internal static async Task RecordAsync(
        FullWorthDbContext db, Guid jobId, IReadOnlyCollection<ImportedEnrichment> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            await using var command = RawSql.Command(connection, """
INSERT INTO "ImportTransactionEnrichments" ("ImportJobId","TransactionId","SetCategoryId","SetTransfer","CreatedAt")
VALUES (@job,@transaction,@category,@transfer,@now)
ON CONFLICT ("ImportJobId","TransactionId") DO NOTHING
""",
                ("@job", jobId), ("@transaction", item.TransactionId),
                ("@category", (object?)item.CategoryId ?? DBNull.Value),
                ("@transfer", item.SetTransfer), ("@now", now));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Wie viele fremde Buchungen dieser Import ergaenzt hat.</summary>
    internal static async Task<int> CountAsync(FullWorthDbContext db, Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT count(*)::int FROM \"ImportTransactionEnrichments\" WHERE \"ImportJobId\"=@job",
            ("@job", jobId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Nimmt zurueck, was dieser Import an fremden Buchungen gesetzt hat - die Buchungen selbst
    /// bleiben stehen. Sie gehoeren ihm nicht.
    /// </summary>
    internal static async Task RevertAsync(FullWorthDbContext db, Guid jobId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;

        // Die Aufteilungen tragen ihre Herkunft selbst. Eine, die der Nutzer seither dazugestellt hat,
        // traegt eine andere (oder gar keine) Auftragskennung und bleibt.
        await using (var allocations = RawSql.Command(connection, """
DELETE FROM "TransactionAllocations" a
USING "ImportTransactionEnrichments" e
WHERE e."ImportJobId"=@job AND a."TransactionId"=e."TransactionId" AND a."CreatedByImportJobId"=@job
""", ("@job", jobId)))
            await allocations.ExecuteNonQueryAsync(ct);

        // EINE Anweisung fuer Kategorie und Umbuchung, nicht zwei nacheinander. Beide Bedingungen muessen
        // den Stand VOR der Ruecknahme sehen: stand die Kategorie zuerst da, setzte ihre Ruecknahme
        // UpdatedAt auf jetzt, und die Umbuchungs-Bedingung darunter ("seither nicht geaendert") fand
        // danach immer eine Aenderung - die eigene. Die Umbuchung wurde so nie zurueckgenommen.
        //
        // Kategorie: IS NOT DISTINCT FROM, nicht "=". Eine Buchung, die nur Aufteilungen bekam, hat bis
        // heute keine eigene Kategorie, und NULL = NULL waere unbekannt statt wahr - die Herkunftsmarke
        // bliebe auf "finanzguru" stehen, obwohl von diesem Import nichts mehr da ist.
        //
        // Umbuchung: sie traegt keine Herkunftsmarke wie die Kategorie. Ob der Nutzer sie seither selbst
        // bestaetigt hat, laesst sich nur daran ablesen, ob die Buchung nach der Ergaenzung noch einmal
        // geaendert wurde. Wurde sie, bleibt die Kennzeichnung stehen - eine unvollstaendige Ruecknahme
        // ist hier der kleinere Fehler als eine, die eine Entscheidung des Nutzers still wieder zu
        // Ausgaben macht.
        await using (var revert = RawSql.Command(connection, """
UPDATE "Transactions" t
SET "CategoryId"           = CASE WHEN t."CategorizationSource"='finanzguru' AND t."CategoryId" IS NOT DISTINCT FROM e."SetCategoryId"
                               THEN NULL ELSE t."CategoryId" END,
    "CategorizationSource" = CASE WHEN t."CategorizationSource"='finanzguru' AND t."CategoryId" IS NOT DISTINCT FROM e."SetCategoryId"
                               THEN 'none' ELSE t."CategorizationSource" END,
    "IsTransfer"           = CASE WHEN e."SetTransfer" AND t."IsTransfer" AND t."UpdatedAt"<=e."CreatedAt"
                               THEN false ELSE t."IsTransfer" END,
    "UpdatedAt"            = @now
FROM "ImportTransactionEnrichments" e
WHERE e."ImportJobId"=@job AND t."Id"=e."TransactionId"
  AND ((t."CategorizationSource"='finanzguru' AND t."CategoryId" IS NOT DISTINCT FROM e."SetCategoryId")
       OR (e."SetTransfer" AND t."IsTransfer" AND t."UpdatedAt"<=e."CreatedAt"))
""", ("@job", jobId), ("@now", now)))
            await revert.ExecuteNonQueryAsync(ct);

        await using (var records = RawSql.Command(connection,
            "DELETE FROM \"ImportTransactionEnrichments\" WHERE \"ImportJobId\"=@job", ("@job", jobId)))
            await records.ExecuteNonQueryAsync(ct);
    }
}
