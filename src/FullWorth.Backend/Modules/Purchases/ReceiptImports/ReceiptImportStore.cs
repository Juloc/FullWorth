using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

public sealed class ReceiptImportStore(FullWorthDbContext db)
{
    public async Task<ReceiptImportBatchRow> CreateBatchAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string sourceType,
        string sourceName,
        string currency,
        bool autoStart,
        Guid? requestedId,
        CancellationToken ct)
    {
        var id = requestedId is { } value && value != Guid.Empty ? value : Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var safeCurrency = NormalizeCurrency(currency);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReceiptImportBatches"
                ("Id", "FullWorthSpaceId", "UserId", "SourceType", "SourceName", "Currency", "Status", "AutoStart", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {fullWorthSpaceId}, {userId}, {Cap(sourceType, 32)}, {Cap(sourceName, 200)}, {safeCurrency}, {ReceiptImportStatuses.Importing}, {autoStart}, {now}, {now})
            ON CONFLICT ("Id") DO NOTHING
            """, ct);
        return await GetBatchRowAsync(userId, fullWorthSpaceId, id, ct)
            ?? throw new InvalidOperationException("Receipt import batch could not be created.");
    }

    public async Task<ReceiptImportItemRow?> FindSourceAsync(Guid fullWorthSpaceId, string sourceType, string externalKey, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportItemProjection>($"""
            SELECT i."Id", i."BatchId", i."FullWorthSpaceId", i."SourceType", i."ExternalKey", i."DisplayName",
                   i."SourceReference", i."ContentFingerprint", i."ReceiptScanJobId", i."PurchaseId", i."Status",
                   i."Error", i."CreatedAt", i."UpdatedAt", j."Status" AS "JobStatus", j."Stage" AS "JobStage",
                   j."Engine" AS "JobEngine", p."ReviewState"
            FROM "ReceiptImportItems" i
            LEFT JOIN "ReceiptScanJobs" j ON j."Id" = i."ReceiptScanJobId"
            LEFT JOIN "Purchases" p ON p."Id" = i."PurchaseId"
            WHERE i."FullWorthSpaceId" = {fullWorthSpaceId}
              AND i."SourceType" = {sourceType}
              AND i."ExternalKey" = {Cap(externalKey, 500)}
            ORDER BY i."CreatedAt" DESC
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : ToRow(rows[0]);
    }

    /// <summary>
    /// Legt die Zeile an - samt dem, was die Quelle ueber den Beleg weiss (#128).
    ///
    /// Gibt es sie schon, wird NICHT einfach nichts getan: die Quelldaten werden aufgefrischt, wenn das
    /// Dokument dort seither geaendert wurde. Das ist die Antwort auf "Aktualisierung ueberschreibt
    /// denselben Beleg statt einen neuen anzulegen" - und sie kostet keine zusaetzliche Anfrage, weil
    /// alles ohnehin in der Liste stand. <c>Created</c> bleibt in diesem Fall false; der Beleg ist
    /// derselbe, und nichts wird ein zweites Mal verarbeitet.
    /// </summary>
    public async Task<(bool Created, ReceiptImportItemRow Item)> CreateItemAsync(
        Guid batchId,
        Guid fullWorthSpaceId,
        string sourceType,
        string externalKey,
        string displayName,
        string? sourceReference,
        string? fingerprint,
        CancellationToken ct,
        ReceiptSourceMetadata? source = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var safeKey = Cap(externalKey, 500);
        var meta = source ?? ReceiptSourceMetadata.None;
        var tagsJson = meta.Tags is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(meta.Tags) : null;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReceiptImportItems"
                ("Id", "BatchId", "FullWorthSpaceId", "SourceType", "ExternalKey", "DisplayName", "SourceReference",
                 "ContentFingerprint", "Status", "CreatedAt", "UpdatedAt",
                 "SourceDocumentDate", "SourceMimeType", "SourceCorrespondent", "SourceTagsJson", "SourceText", "SourceModifiedAt")
            VALUES
                ({id}, {batchId}, {fullWorthSpaceId}, {Cap(sourceType, 32)}, {safeKey}, {Cap(displayName, 500)},
                 {CapNullable(sourceReference, 1000)}, {CapNullable(fingerprint, 64)}, {ReceiptImportItemStatuses.Pending}, {now}, {now},
                 {meta.DocumentDate}, {CapNullable(meta.MimeType, 128)}, {CapNullable(meta.Correspondent, 200)},
                 {tagsJson}::jsonb, {meta.Text}, {meta.ModifiedAt})
            ON CONFLICT ("BatchId", "ExternalKey") DO UPDATE SET
                "DisplayName" = EXCLUDED."DisplayName",
                "SourceDocumentDate" = EXCLUDED."SourceDocumentDate",
                "SourceMimeType" = EXCLUDED."SourceMimeType",
                "SourceCorrespondent" = EXCLUDED."SourceCorrespondent",
                "SourceTagsJson" = EXCLUDED."SourceTagsJson",
                "SourceText" = EXCLUDED."SourceText",
                "SourceModifiedAt" = EXCLUDED."SourceModifiedAt",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            WHERE "ReceiptImportItems"."SourceModifiedAt" IS DISTINCT FROM EXCLUDED."SourceModifiedAt"
            """, ct);

        // ON CONFLICT DO UPDATE meldet ebenfalls eine betroffene Zeile. Ob der Beleg NEU ist, entscheidet
        // deshalb seine Kennung und nicht die Zahl: nur wenn die eben erzeugte Id wirklich in der
        // Tabelle steht, wurde eingefuegt.
        var row = await GetItemAsync(id, ct) ?? await GetItemForBatchAsync(batchId, safeKey, ct);
        return row is null
            ? throw new InvalidOperationException("Receipt import item could not be created or reloaded.")
            : (row.Id == id, row);
    }

    public Task MarkQueuedAsync(Guid itemId, Guid jobId, Guid purchaseId, CancellationToken ct) =>
        UpdateItemAsync(itemId, ReceiptImportItemStatuses.Queued, null, jobId, purchaseId, ct);

    public Task MarkSkippedDuplicateAsync(Guid itemId, string? reason, CancellationToken ct) =>
        UpdateItemAsync(itemId, ReceiptImportItemStatuses.SkippedDuplicate, reason, null, null, ct);

    public Task MarkFailedAsync(Guid itemId, string error, CancellationToken ct) =>
        UpdateItemAsync(itemId, ReceiptImportItemStatuses.Failed, Cap(error, 1000), null, null, ct);

    /// <summary>Records that the batch is paused, or lifts it. Idempotent on purpose.</summary>
    public async Task SetBatchPausedAsync(Guid batchId, DateTimeOffset? pausedAt, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptImportBatches"
            SET "PausedAt" = {pausedAt}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {batchId}
            """, ct);
    }

    /// <summary>
    /// Takes this batch's waiting work back off the queue and returns how many jobs it caught.
    ///
    /// A job in <c>processing</c> is deliberately untouched: it is mid-extraction, and stopping it
    /// there would leave a half-written purchase. The UPDATE is its own race guard — if the worker
    /// claimed a job a moment earlier its status is no longer <c>queued</c>, the row simply is not
    /// matched, and that job finishes. Pause means "take no new work", not "undo what is running".
    /// </summary>
    public async Task<int> WithdrawQueuedJobsAsync(Guid batchId, CancellationToken ct)
    {
        var withdrawn = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs" AS j
            SET "Status" = 'draft', "Stage" = 'draft', "UpdatedAt" = {DateTimeOffset.UtcNow}
            FROM "ReceiptImportItems" AS i
            WHERE i."BatchId" = {batchId} AND i."ReceiptScanJobId" = j."Id" AND j."Status" = 'queued'
            """, ct);

        // The item follows its job back, otherwise the batch reports work as queued that no worker
        // will ever pick up - a screen that lies about what is about to happen.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptImportItems" AS i
            SET "Status" = 'pending', "UpdatedAt" = {DateTimeOffset.UtcNow}
            FROM "ReceiptScanJobs" AS j
            WHERE i."BatchId" = {batchId} AND i."ReceiptScanJobId" = j."Id"
              AND j."Status" = 'draft' AND i."Status" = 'queued'
            """, ct);

        return withdrawn;
    }

    public async Task<ReceiptImportItemRow?> GetItemAsync(Guid batchId, Guid itemId, CancellationToken ct)
    {
        var items = await GetItemsAsync(batchId, ct);
        return items.FirstOrDefault(x => x.Id == itemId);
    }

    public async Task UpdateFingerprintAsync(Guid itemId, string fingerprint, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptImportItems"
            SET "ContentFingerprint" = {Cap(fingerprint, 64)}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {itemId}
            """, ct);
    }


    public async Task<IReadOnlyList<ReceiptImportBatchView>> ListBatchesAsync(Guid userId, Guid fullWorthSpaceId, int limit, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportBatchProjection>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "SourceType", "SourceName", "Currency", "Status", "AutoStart",
                   "CreatedAt", "UpdatedAt", "CompletedAt", "PausedAt", "RolledBackAt"
            FROM "ReceiptImportBatches"
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId} AND "UserId" = {userId}
            ORDER BY "CreatedAt" DESC
            LIMIT {Math.Clamp(limit, 1, 100)}
            """).ToListAsync(ct);

        var result = new List<ReceiptImportBatchView>(rows.Count);
        foreach (var row in rows)
        {
            var view = await GetBatchAsync(userId, fullWorthSpaceId, row.Id, ct);
            if (view is not null) result.Add(view);
        }
        return result;
    }

    public async Task<ReceiptImportBatchView?> GetBatchAsync(Guid userId, Guid fullWorthSpaceId, Guid batchId, CancellationToken ct)
    {
        var batch = await GetBatchRowAsync(userId, fullWorthSpaceId, batchId, ct);
        if (batch is null) return null;
        var items = await GetItemsAsync(batchId, ct);
        var normalized = items.Select(WithEffectiveStatus).ToList();
        var view = BuildView(batch, normalized);
        await PersistDerivedBatchStateAsync(view, ct);
        var refreshedBatch = await GetBatchRowAsync(userId, fullWorthSpaceId, batchId, ct);
        return refreshedBatch is null ? view : view with { Batch = refreshedBatch };
    }











    /// <summary>
    /// Die Dateien eines Kaufs in der Reihenfolge des Scan-Sets (also so, wie der Benutzer sie sortiert
    /// hat), je Datei einmal - ein mehrseitiges PDF steht dort pro Seite, gehoert aber zu einer Datei.
    /// </summary>
    public Task<List<PurchaseSourceFile>> ListPurchaseSourceFilesAsync(Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct) =>
        db.Database.SqlQuery<PurchaseSourceFile>($"""
            SELECT s."StoragePath", s."MimeType", s."OriginalFileName"
            FROM "ReceiptScanSources" s
            JOIN "ReceiptScanJobs" j ON j."Id" = s."ReceiptScanJobId"
            WHERE j."PurchaseId" = {purchaseId} AND j."FullWorthSpaceId" = {fullWorthSpaceId}
            GROUP BY s."StoragePath", s."MimeType", s."OriginalFileName"
            ORDER BY MIN(s."SortOrder")
            """).ToListAsync(ct);

    public async Task<PaperlessUploadRow?> GetPaperlessUploadAsync(Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct) =>
        (await db.Database.SqlQuery<PaperlessUploadRow>($"""
            SELECT "PurchaseId", "Checksum", "TaskId", "UploadedAt"
            FROM "PaperlessUploads"
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId} AND "PurchaseId" = {purchaseId}
            LIMIT 1
            """).ToListAsync(ct)).FirstOrDefault();

    /// <summary>Schreibt die Ablage auf. False, wenn dieser Kauf oder dieselbe Datei schon eingetragen ist.</summary>
    public async Task<bool> AddPaperlessUploadAsync(
        Guid fullWorthSpaceId, Guid purchaseId, Guid userId, string checksum, string? taskId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PaperlessUploads" ("Id", "FullWorthSpaceId", "PurchaseId", "Checksum", "TaskId", "UploadedByUserId", "UploadedAt")
            VALUES ({Guid.NewGuid()}, {fullWorthSpaceId}, {purchaseId}, {checksum}, {CapNullable(taskId, 64)}, {userId}, {DateTimeOffset.UtcNow})
            ON CONFLICT DO NOTHING
            """, ct) == 1;

    public Task<int> RemovePaperlessUploadAsync(Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "PaperlessUploads" WHERE "FullWorthSpaceId" = {fullWorthSpaceId} AND "PurchaseId" = {purchaseId}
            """, ct);

    /// <summary>Welche dieser Paperless-Pruefsummen gehoeren zu Dokumenten, die FullWorth selbst abgelegt hat.</summary>
    public async Task<HashSet<string>> GetOwnPaperlessChecksumsAsync(
        Guid fullWorthSpaceId, IReadOnlyCollection<string> checksums, CancellationToken ct)
    {
        if (checksums.Count == 0) return [];
        var wanted = checksums.Select(x => x.ToLowerInvariant()).Distinct().ToArray();
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT "Checksum" AS "Value" FROM "PaperlessUploads"
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId} AND "Checksum" = ANY ({wanted})
            """).ToListAsync(ct);
        return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<HashSet<int>> GetImportedPaperlessDocumentIdsAsync(
        Guid fullWorthSpaceId,
        string sourcePrefix,
        IReadOnlyCollection<int> documentIds,
        CancellationToken ct)
    {
        if (documentIds.Count == 0) return [];
        var keys = documentIds.Distinct().Select(id => $"{sourcePrefix}:{id}").ToArray();
        var rows = await db.Database.SqlQuery<PaperlessImportedDocumentProjection>($"""
            SELECT DISTINCT CAST(split_part(i."ExternalKey", ':', array_length(string_to_array(i."ExternalKey", ':'), 1)) AS integer) AS "DocumentId"
            FROM "ReceiptImportItems" i
            LEFT JOIN "ReceiptScanJobs" j ON j."Id" = i."ReceiptScanJobId"
            WHERE i."FullWorthSpaceId" = {fullWorthSpaceId}
              AND i."SourceType" = {ReceiptImportSourceTypes.Paperless}
              AND i."ExternalKey" = ANY ({keys})
              AND i."ReceiptScanJobId" IS NOT NULL
              AND COALESCE(j."Status", '') <> {ReceiptScanJobStatuses.Error}
            """).ToListAsync(ct);
        return rows.Select(x => x.DocumentId).ToHashSet();
    }

    private async Task<ReceiptImportBatchRow?> GetBatchRowAsync(Guid userId, Guid fullWorthSpaceId, Guid batchId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportBatchProjection>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "SourceType", "SourceName", "Currency", "Status", "AutoStart",
                   "CreatedAt", "UpdatedAt", "CompletedAt", "PausedAt", "RolledBackAt"
            FROM "ReceiptImportBatches"
            WHERE "Id" = {batchId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "UserId" = {userId}
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : ToRow(rows[0]);
    }

    /// <summary>How many purchases this batch is on record for having created (#141).</summary>
    public Task<int> ImportLinkCountAsync(Guid batchId, CancellationToken ct) =>
        PurchaseImportProvenance.LinkCountAsync(db, batchId, ct);

    /// <summary>
    /// Nimmt die Kaeufe zurueck, die dieser Belegimport-Stapel angelegt hat. Ein Kauf, an dem der Nutzer
    /// seither gearbeitet hat, bleibt stehen - was das heisst, steht im SQL von PurchaseImportProvenance.
    ///
    /// Gibt die Dateipfade der tatsaechlich entfernten Kaeufe zurueck, statt sie selbst zu loeschen: das
    /// ist Dateisystemzugriff, und der gehoert in den Service, der auch den PurchaseStorage-Pfad kennt.
    /// Belegdatei-lose Zeilen in "ReceiptImportItems" verlieren ihren Zeiger auf den geloeschten Kauf,
    /// statt auf eine nicht mehr existierende Zeile zu zeigen - die Spalte hat ohnehin keinen Fremdschluessel.
    /// </summary>
    public async Task<(int Removed, IReadOnlyList<string> FilePaths)> RollbackBatchAsync(Guid batchId, CancellationToken ct)
    {
        var linkedIds = await PurchaseImportProvenance.LinkedPurchaseIdsAsync(db, batchId, ct);
        var documentPaths = await PurchaseImportProvenance.DocumentPathsAsync(db, linkedIds, ct);

        var removed = await PurchaseImportProvenance.DeleteAsync(db, batchId, PurchaseImportSources.Receipt, ct);
        var removedIds = removed.Select(x => x.PurchaseId).ToHashSet();
        var filePaths = documentPaths
            .Where(x => removedIds.Contains(x.PurchaseId))
            .Select(x => x.StoragePath)
            .Concat(removed.Select(x => x.ReceiptImagePath).Where(path => !string.IsNullOrWhiteSpace(path))!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (removedIds.Count > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ReceiptImportItems" SET "PurchaseId" = NULL, "UpdatedAt" = {DateTimeOffset.UtcNow}
                WHERE "PurchaseId" = ANY({removedIds.ToArray()})
                """, ct);

        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptImportBatches"
            SET "Status" = {ReceiptImportStatuses.RolledBack}, "RolledBackAt" = {now}, "UpdatedAt" = {now}
            WHERE "Id" = {batchId}
            """, ct);

        return (removed.Count, filePaths);
    }

    /// <summary>
    /// Die Paperless-Belege, die noch heruntergeladen werden muessen (#127).
    ///
    /// Das ist der Wiederaufnahmepunkt: was hier nicht mehr auftaucht, ist erledigt. Ein Neustart
    /// mitten im Import faengt genau hier weiter an, ohne eine einzige Anfrage fuer das zu stellen,
    /// was schon da ist - und ein pausierter Stapel liefert nichts, weil Pause "keine neue Arbeit"
    /// heisst.
    /// </summary>
    public async Task<IReadOnlyList<PendingPaperlessItem>> ListPendingPaperlessItemsAsync(int limit, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PendingPaperlessItem>($"""
            SELECT i."Id" AS "ItemId", i."BatchId", i."FullWorthSpaceId", i."SourceReference", b."UserId", b."Currency", b."AutoStart", b."PausedAt"
            FROM "ReceiptImportItems" i
            JOIN "ReceiptImportBatches" b ON b."Id" = i."BatchId"
            WHERE i."SourceType" = {ReceiptImportSourceTypes.Paperless}
              AND i."Status" = {ReceiptImportItemStatuses.Pending}
              AND i."ReceiptScanJobId" IS NULL
              AND b."PausedAt" IS NULL
            ORDER BY i."CreatedAt", i."Id"
            LIMIT {limit}
            """).ToListAsync(ct);
        return rows;
    }

    private async Task<ReceiptImportItemRow?> GetItemForBatchAsync(Guid batchId, string externalKey, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportItemProjection>($"""
            SELECT i."Id", i."BatchId", i."FullWorthSpaceId", i."SourceType", i."ExternalKey", i."DisplayName",
                   i."SourceReference", i."ContentFingerprint", i."ReceiptScanJobId", i."PurchaseId", i."Status",
                   i."Error", i."CreatedAt", i."UpdatedAt", j."Status" AS "JobStatus", j."Stage" AS "JobStage",
                   j."Engine" AS "JobEngine", p."ReviewState"
            FROM "ReceiptImportItems" i
            LEFT JOIN "ReceiptScanJobs" j ON j."Id" = i."ReceiptScanJobId"
            LEFT JOIN "Purchases" p ON p."Id" = i."PurchaseId"
            WHERE i."BatchId" = {batchId} AND i."ExternalKey" = {externalKey}
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : ToRow(rows[0]);
    }

    private async Task<ReceiptImportItemRow?> GetItemAsync(Guid itemId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportItemProjection>($"""
            SELECT i."Id", i."BatchId", i."FullWorthSpaceId", i."SourceType", i."ExternalKey", i."DisplayName",
                   i."SourceReference", i."ContentFingerprint", i."ReceiptScanJobId", i."PurchaseId", i."Status",
                   i."Error", i."CreatedAt", i."UpdatedAt", j."Status" AS "JobStatus", j."Stage" AS "JobStage",
                   j."Engine" AS "JobEngine", p."ReviewState"
            FROM "ReceiptImportItems" i
            LEFT JOIN "ReceiptScanJobs" j ON j."Id" = i."ReceiptScanJobId"
            LEFT JOIN "Purchases" p ON p."Id" = i."PurchaseId"
            WHERE i."Id" = {itemId}
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : ToRow(rows[0]);
    }

    private async Task<List<ReceiptImportItemRow>> GetItemsAsync(Guid batchId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportItemProjection>($"""
            SELECT i."Id", i."BatchId", i."FullWorthSpaceId", i."SourceType", i."ExternalKey", i."DisplayName",
                   i."SourceReference", i."ContentFingerprint", i."ReceiptScanJobId", i."PurchaseId", i."Status",
                   i."Error", i."CreatedAt", i."UpdatedAt", j."Status" AS "JobStatus", j."Stage" AS "JobStage",
                   j."Engine" AS "JobEngine", p."ReviewState"
            FROM "ReceiptImportItems" i
            LEFT JOIN "ReceiptScanJobs" j ON j."Id" = i."ReceiptScanJobId"
            LEFT JOIN "Purchases" p ON p."Id" = i."PurchaseId"
            WHERE i."BatchId" = {batchId}
            ORDER BY i."CreatedAt", i."Id"
            """).ToListAsync(ct);
        return rows.Select(ToRow).ToList();
    }

    private async Task UpdateItemAsync(Guid itemId, string status, string? error, Guid? jobId, Guid? purchaseId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptImportItems"
            SET "Status" = {status},
                "Error" = {error},
                "ReceiptScanJobId" = COALESCE({jobId}, "ReceiptScanJobId"),
                "PurchaseId" = COALESCE({purchaseId}, "PurchaseId"),
                "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {itemId}
            """, ct);
    }

    private async Task PersistDerivedBatchStateAsync(ReceiptImportBatchView view, CancellationToken ct)
    {
        var terminal = view.Total > 0 && view.Queued == 0 && view.Processing == 0;
        var status = !terminal
            ? ReceiptImportStatuses.Processing
            : view.Failed > 0 ? ReceiptImportStatuses.CompletedWithErrors : ReceiptImportStatuses.Completed;
        var completedAt = terminal ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptImportBatches"
            SET "Status" = {status}, "UpdatedAt" = {DateTimeOffset.UtcNow},
                "CompletedAt" = {completedAt}
            WHERE "Id" = {view.Batch.Id}
            """, ct);
    }

    private static ReceiptImportBatchView BuildView(ReceiptImportBatchRow batch, IReadOnlyList<ReceiptImportItemRow> items) => new(
        batch,
        items.Count,
        items.Count(x => x.Status == ReceiptImportItemStatuses.Queued || x.Status == ReceiptImportItemStatuses.Pending),
        items.Count(x => x.Status == ReceiptImportItemStatuses.Processing),
        items.Count(x => x.Status == ReceiptImportItemStatuses.Done),
        items.Count(x => x.Status == ReceiptImportItemStatuses.NeedsReview),
        items.Count(x => x.Status == ReceiptImportItemStatuses.SkippedDuplicate),
        items.Count(x => x.Status == ReceiptImportItemStatuses.Failed),
        items);

    private static ReceiptImportItemRow WithEffectiveStatus(ReceiptImportItemRow item)
    {
        if (item.Status == ReceiptImportItemStatuses.SkippedDuplicate) return item;
        if (!item.ReceiptScanJobId.HasValue && item.Status == ReceiptImportItemStatuses.Failed) return item;
        var effective = item.JobStatus switch
        {
            ReceiptScanJobStatuses.Draft => ReceiptImportItemStatuses.Pending,
            ReceiptScanJobStatuses.Queued => ReceiptImportItemStatuses.Queued,
            ReceiptScanJobStatuses.Processing => ReceiptImportItemStatuses.Processing,
            ReceiptScanJobStatuses.Error => ReceiptImportItemStatuses.Failed,
            ReceiptScanJobStatuses.Done when string.Equals(item.ReviewState, "needs_review", StringComparison.OrdinalIgnoreCase) => ReceiptImportItemStatuses.NeedsReview,
            ReceiptScanJobStatuses.Done => ReceiptImportItemStatuses.Done,
            _ => item.Status
        };
        return item with { Status = effective };
    }

    private static ReceiptImportBatchRow ToRow(ReceiptImportBatchProjection x) =>
        new(x.Id, x.FullWorthSpaceId, x.UserId, x.SourceType, x.SourceName, x.Currency, x.Status, x.AutoStart, x.CreatedAt, x.UpdatedAt, x.CompletedAt, x.PausedAt, x.RolledBackAt);

    private static ReceiptImportItemRow ToRow(ReceiptImportItemProjection x) =>
        new(x.Id, x.BatchId, x.FullWorthSpaceId, x.SourceType, x.ExternalKey, x.DisplayName, x.SourceReference,
            x.ContentFingerprint, x.ReceiptScanJobId, x.PurchaseId, x.Status, x.Error, x.CreatedAt, x.UpdatedAt, x.JobStatus, x.ReviewState,
            x.JobStage, x.JobEngine);

    private static string NormalizeCurrency(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Import batch currency must be a three-letter code.", nameof(value));
        return normalized;
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
    private static string? CapNullable(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : Cap(value.Trim(), max);

    private sealed class ReceiptImportBatchProjection
    {
        public Guid Id { get; set; }
        public Guid FullWorthSpaceId { get; set; }
        public Guid UserId { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public string Currency { get; set; } = "EUR";
        public string Status { get; set; } = string.Empty;
        public bool AutoStart { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? PausedAt { get; set; }
        public DateTimeOffset? RolledBackAt { get; set; }
    }

    private sealed class ReceiptImportItemProjection
    {
        public Guid Id { get; set; }
        public Guid BatchId { get; set; }
        public Guid FullWorthSpaceId { get; set; }
        public string SourceType { get; set; } = string.Empty;
        public string ExternalKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? SourceReference { get; set; }
        public string? ContentFingerprint { get; set; }
        public Guid? ReceiptScanJobId { get; set; }
        public Guid? PurchaseId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Error { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? JobStatus { get; set; }
        // #129: welcher Schritt gerade laeuft und womit. Der Scan-Job ist ohnehin schon gejoint -
        // diese beiden Spalten mitzunehmen kostet nichts und ist der Unterschied zwischen "wird
        // verarbeitet" und "OCR laeuft gerade".
        public string? JobStage { get; set; }
        public string? JobEngine { get; set; }
        public string? ReviewState { get; set; }
    }

    private sealed class PaperlessImportedDocumentProjection
    {
        public int DocumentId { get; set; }
    }
}
