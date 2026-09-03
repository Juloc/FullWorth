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
                   i."Error", i."CreatedAt", i."UpdatedAt", j."Status" AS "JobStatus", p."ReviewState"
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

    public async Task<(bool Created, ReceiptImportItemRow Item)> CreateItemAsync(
        Guid batchId,
        Guid fullWorthSpaceId,
        string sourceType,
        string externalKey,
        string displayName,
        string? sourceReference,
        string? fingerprint,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var safeKey = Cap(externalKey, 500);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReceiptImportItems"
                ("Id", "BatchId", "FullWorthSpaceId", "SourceType", "ExternalKey", "DisplayName", "SourceReference",
                 "ContentFingerprint", "Status", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {batchId}, {fullWorthSpaceId}, {Cap(sourceType, 32)}, {safeKey}, {Cap(displayName, 500)},
                 {CapNullable(sourceReference, 1000)}, {CapNullable(fingerprint, 64)}, {ReceiptImportItemStatuses.Pending}, {now}, {now})
            ON CONFLICT ("BatchId", "ExternalKey") DO NOTHING
            """, ct);

        var row = affected > 0
            ? await GetItemAsync(id, ct)
            : await GetItemForBatchAsync(batchId, safeKey, ct);
        return row is null
            ? throw new InvalidOperationException("Receipt import item could not be created or reloaded.")
            : (affected > 0, row);
    }

    public Task MarkQueuedAsync(Guid itemId, Guid jobId, Guid purchaseId, CancellationToken ct) =>
        UpdateItemAsync(itemId, ReceiptImportItemStatuses.Queued, null, jobId, purchaseId, ct);

    public Task MarkSkippedDuplicateAsync(Guid itemId, string? reason, CancellationToken ct) =>
        UpdateItemAsync(itemId, ReceiptImportItemStatuses.SkippedDuplicate, reason, null, null, ct);

    public Task MarkFailedAsync(Guid itemId, string error, CancellationToken ct) =>
        UpdateItemAsync(itemId, ReceiptImportItemStatuses.Failed, Cap(error, 1000), null, null, ct);

    public async Task UpdateFingerprintAsync(Guid itemId, string fingerprint, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptImportItems"
            SET "ContentFingerprint" = {Cap(fingerprint, 64)}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {itemId}
            """, ct);
    }

    public async Task TouchPaperlessSyncAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PaperlessReceiptConnections"
            SET "LastSyncAt" = {DateTimeOffset.UtcNow}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId}
            """, ct);
    }

    public async Task<IReadOnlyList<ReceiptImportBatchView>> ListBatchesAsync(Guid userId, Guid fullWorthSpaceId, int limit, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportBatchProjection>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "SourceType", "SourceName", "Currency", "Status", "AutoStart",
                   "CreatedAt", "UpdatedAt", "CompletedAt"
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

    public async Task<PaperlessStoredConnection?> GetPaperlessConnectionAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PaperlessConnectionProjection>($"""
            SELECT "FullWorthSpaceId", "UserId", "BaseUrl", "ApiTokenProtected", "DefaultQuery", "IsEnabled",
                   "LastSyncAt", "CreatedAt", "UpdatedAt"
            FROM "PaperlessReceiptConnections"
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId}
            LIMIT 1
            """).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var x = rows[0];
        return new PaperlessStoredConnection(x.FullWorthSpaceId, x.UserId, x.BaseUrl, x.ApiTokenProtected, x.DefaultQuery, x.IsEnabled, x.LastSyncAt, x.CreatedAt, x.UpdatedAt);
    }

    public async Task UpsertPaperlessConnectionAsync(
        Guid fullWorthSpaceId,
        Guid userId,
        string baseUrl,
        string protectedToken,
        string? defaultQuery,
        bool enabled,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PaperlessReceiptConnections"
                ("FullWorthSpaceId", "UserId", "BaseUrl", "ApiTokenProtected", "DefaultQuery", "IsEnabled", "CreatedAt", "UpdatedAt")
            VALUES ({fullWorthSpaceId}, {userId}, {Cap(baseUrl, 1000)}, {protectedToken}, {CapNullable(defaultQuery, 1000)}, {enabled}, {now}, {now})
            ON CONFLICT ("FullWorthSpaceId") DO UPDATE SET
                "UserId" = EXCLUDED."UserId",
                "BaseUrl" = EXCLUDED."BaseUrl",
                "ApiTokenProtected" = EXCLUDED."ApiTokenProtected",
                "DefaultQuery" = EXCLUDED."DefaultQuery",
                "IsEnabled" = EXCLUDED."IsEnabled",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);
    }

    public async Task DeletePaperlessConnectionAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "PaperlessReceiptConnections" WHERE "FullWorthSpaceId" = {fullWorthSpaceId}
            """, ct);
    }

    private async Task<ReceiptImportBatchRow?> GetBatchRowAsync(Guid userId, Guid fullWorthSpaceId, Guid batchId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportBatchProjection>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "SourceType", "SourceName", "Currency", "Status", "AutoStart",
                   "CreatedAt", "UpdatedAt", "CompletedAt"
            FROM "ReceiptImportBatches"
            WHERE "Id" = {batchId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "UserId" = {userId}
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : ToRow(rows[0]);
    }

    private async Task<ReceiptImportItemRow?> GetItemForBatchAsync(Guid batchId, string externalKey, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ReceiptImportItemProjection>($"""
            SELECT i."Id", i."BatchId", i."FullWorthSpaceId", i."SourceType", i."ExternalKey", i."DisplayName",
                   i."SourceReference", i."ContentFingerprint", i."ReceiptScanJobId", i."PurchaseId", i."Status",
                   i."Error", i."CreatedAt", i."UpdatedAt", j."Status" AS "JobStatus", p."ReviewState"
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
                   i."Error", i."CreatedAt", i."UpdatedAt", j."Status" AS "JobStatus", p."ReviewState"
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
                   i."Error", i."CreatedAt", i."UpdatedAt", j."Status" AS "JobStatus", p."ReviewState"
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
        new(x.Id, x.FullWorthSpaceId, x.UserId, x.SourceType, x.SourceName, x.Currency, x.Status, x.AutoStart, x.CreatedAt, x.UpdatedAt, x.CompletedAt);

    private static ReceiptImportItemRow ToRow(ReceiptImportItemProjection x) =>
        new(x.Id, x.BatchId, x.FullWorthSpaceId, x.SourceType, x.ExternalKey, x.DisplayName, x.SourceReference,
            x.ContentFingerprint, x.ReceiptScanJobId, x.PurchaseId, x.Status, x.Error, x.CreatedAt, x.UpdatedAt, x.JobStatus, x.ReviewState);

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
        public string? ReviewState { get; set; }
    }

    private sealed class PaperlessConnectionProjection
    {
        public Guid FullWorthSpaceId { get; set; }
        public Guid UserId { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiTokenProtected { get; set; } = string.Empty;
        public string? DefaultQuery { get; set; }
        public bool IsEnabled { get; set; }
        public DateTimeOffset? LastSyncAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}

public sealed record PaperlessStoredConnection(
    Guid FullWorthSpaceId,
    Guid UserId,
    string BaseUrl,
    string ApiTokenProtected,
    string? DefaultQuery,
    bool IsEnabled,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
