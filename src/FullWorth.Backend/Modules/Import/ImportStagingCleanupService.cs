using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Wie lange liegen gebliebene Import-Staging-Zeilen aufbewahrt werden, bevor
/// <see cref="ImportStagingCleanupService"/> sie raeumt (#142).
///
/// Weder Abbrechen noch Ruecknahme loeschten bisher je eine Zeile: "ImportCandidates" haelt
/// Empfaenger und Verwendungszweck im Klartext (varchar 500/2000), und die ueberlebten eine
/// Ruecknahme, die der Nutzer ausdruecklich verlangt hatte. Ein Aufbewahrungsfenster statt
/// sofortigem Loeschen bei Cancel/Rollback ist bewusst: ein Codepfad, ein Test, keine Sonderregel
/// fuer "gerade erst abgebrochen".
/// </summary>
public sealed class ImportStagingRetentionOptions
{
    public const string SectionName = "ImportStagingRetention";

    /// <summary>
    /// Wie viele Tage seit dem letzten Statuswechsel eines Auftrags (dessen "UpdatedAt") vergehen
    /// muessen, bevor seine Staging-Zeilen geraeumt werden.
    /// </summary>
    public int RetentionDays { get; set; } = 30;
}

/// <summary>
/// Raeumt Import-Staging auf (#142): "ImportCandidates"-Zeilen abgebrochener, zurueckgenommener,
/// liegen gebliebener oder folgenlos abgeschlossener Auftraege, den am Auftrag aufbewahrten
/// Dateiinhalt (#131) sowie den OCR-Volltext alter Beleg-Importstapel.
///
/// "ImportJobs"-Zeilen selbst bleiben immer stehen - sie sind der Auftragsverlauf, und
/// "ImportJobStore" liest fuer die Verlaufsanzeige nie wieder aus "ImportCandidates" zurueck, nur aus
/// "ImportJobs" selbst und aus "ImportTransactionLinks". Ein Auftrag, der noch rueckgaengig gemacht
/// werden kann (siehe "rollbackAvailable" in <see cref="ImportJobStore"/>), wird nie angefasst, egal
/// wie alt er ist: seine Kandidatenzeilen speisen die Detailansicht, in der der Nutzer nachsieht, was
/// ein noch offener Import enthaelt.
/// </summary>
public sealed class ImportStagingCleanupService(FullWorthDbContext db)
{
    /// <summary>
    /// Loescht "ImportCandidates"-Zeilen abgebrochener, zurueckgenommener, liegen gebliebener oder
    /// folgenlos abgeschlossener Auftraege, deren letzter Statuswechsel laenger als
    /// <paramref name="retentionDays"/> zurueckliegt, und nimmt denselben Auftraegen den aufbewahrten
    /// Dateiinhalt ab.
    ///
    /// Ein abgebrochener oder zurueckgenommener Auftrag ist immer faellig, sobald er alt genug ist -
    /// seine Kandidatenzeilen SIND der Klartext-Rest, den der Nutzer loswerden wollte. Dasselbe gilt
    /// fuer einen Zwischenstand ('ready'), den nie jemand festgeschrieben hat: aus ihm wurde nichts,
    /// und beim Finanzguru-Weg haengt zusaetzlich die gelesene Datei daran (#131). Ein abgeschlossener
    /// Auftrag bleibt unangetastet, solange er noch verknuepfte oder ergaenzte Buchungen hat
    /// (<see cref="ImportTransactionProvenance.LinkCountAsync"/>,
    /// <see cref="ImportTransactionEnrichment.CountAsync"/>) - erst ohne beides ist er nachweislich
    /// folgenlos oder bereits vollstaendig zurueckgenommen.
    /// </summary>
    /// <returns>Die Zahl geloeschter "ImportCandidates"-Zeilen.</returns>
    public async Task<int> PurgeStaleCandidatesAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, retentionDays));
        var connection = await RawSql.OpenAsync(db, ct);

        var candidateJobs = new List<(Guid Id, string Status)>();
        await using (var select = RawSql.Command(connection,
            "SELECT \"Id\",\"Status\" FROM \"ImportJobs\" WHERE \"UpdatedAt\"<@cutoff AND \"Status\" IN ('cancelled','rolled_back','completed','ready')",
            ("@cutoff", cutoff)))
        await using (var reader = await select.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                candidateJobs.Add((RawSql.Guid(reader, "Id"), RawSql.String(reader, "Status")));

        var eligible = new List<Guid>();
        foreach (var (id, status) in candidateJobs)
        {
            // Ein liegen gebliebener Zwischenstand ('ready') ist immer faellig, sobald er alt genug
            // ist: aus ihm wurde nichts geschrieben, und er haelt genau das, was der Nutzer nicht
            // aufbewahrt sehen will - Empfaenger und Verwendungszweck im Klartext, beim
            // Finanzguru-Weg dazu die gelesene Datei selbst (#131).
            //
            // "Folgenlos" heisst: weder eine Buchung erzeugt noch eine ergaenzt. Ein Finanzguru-Auftrag,
            // der nur vorhandenen Bankbuchungen Kategorien nachgetragen hat, laesst sich noch
            // zuruecknehmen und ist deshalb so wenig faellig wie einer mit verknuepften Buchungen.
            if (status != "completed"
                || (await ImportTransactionProvenance.LinkCountAsync(db, id, ct) == 0
                    && await ImportTransactionEnrichment.CountAsync(db, id, ct) == 0))
                eligible.Add(id);
        }
        if (eligible.Count == 0) return 0;

        var ids = eligible.ToArray();
        int deleted;
        await using (var delete = RawSql.Command(connection,
            "DELETE FROM \"ImportCandidates\" WHERE \"ImportJobId\"=ANY(@ids)", ("@ids", ids)))
            deleted = await delete.ExecuteNonQueryAsync(ct);

        // Der Auftrag behaelt seine Zeile, aber nicht mehr die gelesene Datei, und ein liegen
        // gebliebener Zwischenstand hoert auf, einer zu sein: ohne Kandidatenzeilen gaebe es nichts
        // mehr festzuschreiben, und 'ready' zu lassen hiesse, einen Knopf anzubieten, der nur noch
        // "nichts uebernommen" antworten kann.
        await using (var finish = RawSql.Command(connection, """
UPDATE "ImportJobs"
SET "SourcePayloadEncrypted"=NULL,
    "Status"=CASE WHEN "Status"='ready' THEN 'cancelled' ELSE "Status" END
WHERE "Id"=ANY(@ids) AND ("SourcePayloadEncrypted" IS NOT NULL OR "Status"='ready')
""", ("@ids", ids)))
            await finish.ExecuteNonQueryAsync(ct);

        return deleted;
    }

    /// <summary>
    /// Nullt den OCR-Volltext ("SourceText") alter, abgeschlossener Beleg-Importstapel (#142).
    /// "ExternalKey", "ContentFingerprint" und "PurchaseId" bleiben stehen - ein Reimport macht daran
    /// seine Dublettenpruefung, und nur der Volltext selbst ist das aufbewahrungswuerdige Risiko
    /// (die komplette Paperless-OCR je Dokument).
    ///
    /// Enger, kommentierter Griff ueber die Modulgrenze hinweg, wie ihn "Reconciliation" und
    /// "DataErasure" schon haben: keine Typen aus Modules/Purchases importiert, nur Tabellen- und
    /// Spaltennamen in reinem SQL, keine Fachlogik. "ReceiptImportBatches" kennt heute (vor #141) noch
    /// keinen eigenen "rolled_back"/"cancelled"-Status - "completed"/"completed_with_errors"/"failed"
    /// (siehe <c>ReceiptImportStatuses</c> in Modules/Purchases/ReceiptImports/ReceiptImportModels.cs)
    /// sind die einzigen Endzustaende, und genau die zaehlen hier als abgeschlossen; "importing" und
    /// "processing" werden nie angefasst.
    /// </summary>
    public Task<int> ScrubStaleReceiptSourceTextAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, retentionDays));
        return db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "ReceiptImportItems" i
SET "SourceText" = NULL
FROM "ReceiptImportBatches" b
WHERE i."BatchId" = b."Id"
  AND b."Status" IN ('completed','completed_with_errors','failed')
  AND b."UpdatedAt" < {cutoff}
  AND i."SourceText" IS NOT NULL
""", ct);
    }
}

/// <summary>
/// 24-Stunden-Schleife fuer <see cref="ImportStagingCleanupService"/>, nach dem Muster von
/// <c>NetWorthSnapshotWorker</c> (Modules/Portfolio/NetWorthSnapshotService.cs): eigener Scope je
/// Durchlauf, ein fehlgeschlagener Durchlauf beendet den Prozess nicht.
/// </summary>
public sealed class ImportStagingCleanupWorker(
    IServiceScopeFactory scopes,
    IOptions<ImportStagingRetentionOptions> options,
    ILogger<ImportStagingCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ImportStagingCleanupService>();
                var retentionDays = options.Value.RetentionDays;
                var purgedCandidates = await service.PurgeStaleCandidatesAsync(retentionDays, stoppingToken);
                var scrubbedReceipts = await service.ScrubStaleReceiptSourceTextAsync(retentionDays, stoppingToken);
                if (purgedCandidates > 0 || scrubbedReceipts > 0)
                    logger.LogInformation(
                        "Import staging cleanup purged {PurgedCandidates} candidate row(s) and scrubbed source text on {ScrubbedReceipts} receipt item(s).",
                        purgedCandidates, scrubbedReceipts);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Import staging cleanup failed."); }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
