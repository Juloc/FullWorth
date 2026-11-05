using System.Data;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed class ReceiptScanJobStore(FullWorthDbContext db, ReceiptScanQueueSignal? signal = null)
{
    public async Task CreateAsync(ReceiptScanJobRow job, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReceiptScanJobs"
                ("Id", "FullWorthSpaceId", "UserId", "PurchaseId", "FileName", "ContentType", "Status", "Stage",
                 "Engine", "Error", "WarningsJson", "Attempts", "CreatedAt", "StartedAt", "CompletedAt", "UpdatedAt")
            VALUES
                ({job.Id}, {job.FullWorthSpaceId}, {job.UserId}, {job.PurchaseId}, {job.FileName}, {job.ContentType},
                 {job.Status}, {job.Stage}, {job.Engine}, {job.Error}, {job.WarningsJson}, {job.Attempts}, {job.CreatedAt},
                 {job.StartedAt}, {job.CompletedAt}, {job.UpdatedAt})
            """, ct);
    }

    // One select for every read of a job, so a column added to the view is added once. The queue
    // position counts the queued jobs ahead of this one across all spaces - the worker is global FIFO.
    private const string ViewSelect = """
        SELECT j."Id", j."FullWorthSpaceId", j."UserId", j."PurchaseId", j."FileName", j."ContentType",
               j."Status", j."Stage", j."Engine", j."ReaderVersion", j."Reason", j."Error", j."WarningsJson", j."Attempts",
               (SELECT COUNT(*)::integer FROM "ReceiptScanSources" s WHERE s."ReceiptScanJobId" = j."Id") AS "SourceCount",
               CASE WHEN j."Status" = 'queued' THEN
                    (SELECT COUNT(*)::integer + 1 FROM "ReceiptScanJobs" q
                     WHERE q."Status" = 'queued' AND (q."CreatedAt", q."Id") < (j."CreatedAt", j."Id"))
               END AS "QueuePosition",
               j."CreatedAt", j."StartedAt", j."CompletedAt", j."UpdatedAt",
               p."Merchant", p."TotalAmount", p."Currency", p."PurchaseDate", p."Status" AS "PurchaseStatus",
               (SELECT COUNT(*)::integer FROM "PurchaseItems" i WHERE i."PurchaseId" = p."Id") AS "ItemCount"
        FROM "ReceiptScanJobs" j
        JOIN "Purchases" p ON p."Id" = j."PurchaseId"
        """;

    /// <summary>
    /// The jobs of one list: <c>queue</c> (waiting or being read, in the order the worker takes them),
    /// <c>history</c> (finished, newest first), <c>review</c> (read, but the purchase is not confirmed
    /// yet) or <c>failed</c>. Drafts are a user's unfinished scan sets and belong to no list.
    /// </summary>
    public Task<List<ReceiptScanJobView>> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, string scope, int limit, int offset, CancellationToken ct) =>
        db.Database.SqlQueryRaw<ReceiptScanJobView>(ViewSelect + """

            WHERE j."UserId" = {0} AND j."FullWorthSpaceId" = {1}
              AND CASE {2}
                    WHEN 'queue' THEN j."Status" IN ('queued', 'processing')
                    WHEN 'history' THEN j."Status" = 'done'
                    WHEN 'failed' THEN j."Status" = 'error'
                    ELSE j."Status" = 'done' AND p."Status" <> 'confirmed'
                  END
            ORDER BY
              CASE WHEN {2} = 'queue' AND j."Status" = 'processing' THEN 0 ELSE 1 END,
              CASE WHEN {2} = 'queue' THEN j."CreatedAt" END ASC,
              COALESCE(j."CompletedAt", j."UpdatedAt") DESC
            LIMIT {3} OFFSET {4}
            """, userId, fullWorthSpaceId, scope, Math.Clamp(limit, 1, 100), Math.Max(0, offset)).ToListAsync(ct);

    public Task<ReceiptScanCounts> CountsForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Database.SqlQuery<ReceiptScanCounts>($"""
            SELECT COUNT(*) FILTER (WHERE j."Status" = 'queued')::integer AS "Queued",
                   COUNT(*) FILTER (WHERE j."Status" = 'processing')::integer AS "Processing",
                   COUNT(*) FILTER (WHERE j."Status" = 'error')::integer AS "Failed",
                   COUNT(*) FILTER (WHERE j."Status" = 'done' AND p."Status" <> 'confirmed')::integer AS "NeedsReview"
            FROM "ReceiptScanJobs" j
            JOIN "Purchases" p ON p."Id" = j."PurchaseId"
            WHERE j."UserId" = {userId} AND j."FullWorthSpaceId" = {fullWorthSpaceId}
            """).SingleAsync(ct);

    public Task<ReceiptScanJobView?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct) =>
        db.Database.SqlQueryRaw<ReceiptScanJobView>(ViewSelect + """

            WHERE j."Id" = {0} AND j."UserId" = {1} AND j."FullWorthSpaceId" = {2}
            """, jobId, userId, fullWorthSpaceId).SingleOrDefaultAsync(ct);

    public Task<List<ReceiptScanSourceRow>> ListSourcesAsync(Guid jobId, CancellationToken ct) =>
        db.Database.SqlQuery<ReceiptScanSourceRow>($"""
            SELECT "Id", "ReceiptScanJobId", "PurchaseDocumentId", "SortOrder", "SourceType",
                   "OriginalFileName", "MimeType", "StoragePath", "PageNumber", "Fingerprint",
                   "SizeBytes", "CreatedAt", "UpdatedAt"
            FROM "ReceiptScanSources"
            WHERE "ReceiptScanJobId" = {jobId}
            ORDER BY "SortOrder", "Id"
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<ReceiptScanSourceRow>?> ListSourcesForUserAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        if (await GetForUserAsync(userId, fullWorthSpaceId, jobId, ct) is null) return null;
        return await ListSourcesAsync(jobId, ct);
    }

    public async Task CreateSourcesAsync(IEnumerable<ReceiptScanSourceRow> sources, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ReceiptScanSources"
                    ("Id", "ReceiptScanJobId", "PurchaseDocumentId", "SortOrder", "SourceType",
                     "OriginalFileName", "MimeType", "StoragePath", "PageNumber", "Fingerprint",
                     "SizeBytes", "CreatedAt", "UpdatedAt")
                VALUES
                    ({source.Id}, {source.ReceiptScanJobId}, {source.PurchaseDocumentId}, {source.SortOrder}, {source.SourceType},
                     {source.OriginalFileName}, {source.MimeType}, {source.StoragePath}, {source.PageNumber}, {source.Fingerprint},
                     {source.SizeBytes}, {source.CreatedAt}, {source.UpdatedAt})
                """, ct);
        }
    }

    public Task<int> DeleteSourceAsync(Guid jobId, Guid sourceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "ReceiptScanSources" WHERE "Id" = {sourceId} AND "ReceiptScanJobId" = {jobId}
            """, ct);

    public async Task ReorderSourcesAsync(Guid jobId, IReadOnlyList<Guid> sourceIds, CancellationToken ct)
    {
        if (sourceIds.Count == 0) return;
        // Move the whole set to a collision-free range first because SortOrder has a unique index per job.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanSources"
            SET "SortOrder" = "SortOrder" + 1000000, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "ReceiptScanJobId" = {jobId}
            """, ct);
        for (var index = 0; index < sourceIds.Count; index++)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ReceiptScanSources"
                SET "SortOrder" = {index}, "UpdatedAt" = {DateTimeOffset.UtcNow}
                WHERE "ReceiptScanJobId" = {jobId} AND "Id" = {sourceIds[index]}
                """, ct);
        }
    }

    public async Task UpdateSourceAsync(ReceiptScanSourceRow source, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanSources"
            SET "PurchaseDocumentId" = {source.PurchaseDocumentId}, "SourceType" = {source.SourceType},
                "OriginalFileName" = {source.OriginalFileName}, "MimeType" = {source.MimeType},
                "StoragePath" = {source.StoragePath}, "PageNumber" = {source.PageNumber},
                "Fingerprint" = {source.Fingerprint}, "SizeBytes" = {source.SizeBytes},
                "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {source.Id} AND "ReceiptScanJobId" = {source.ReceiptScanJobId}
            """, ct);
    }

    public async Task<int> NextSortOrderAsync(Guid jobId, CancellationToken ct) =>
        await db.Database.SqlQuery<int>($"""
            SELECT COALESCE(MAX("SortOrder"), -1) + 1 AS "Value"
            FROM "ReceiptScanSources" WHERE "ReceiptScanJobId" = {jobId}
            """).SingleAsync(ct);

    public Task<int> SetWarningsAsync(Guid jobId, string? warningsJson, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs" SET "WarningsJson" = {warningsJson}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {jobId}
            """, ct);

    public async Task<bool> StartAsync(Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs" AS j
            SET "Status" = 'queued', "Stage" = 'queued', "Engine" = NULL, "ReaderVersion" = NULL, "Reason" = NULL, "Lane" = NULL, "Error" = NULL,
                "CompletedAt" = NULL, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE j."Id" = {jobId} AND j."UserId" = {userId} AND j."FullWorthSpaceId" = {fullWorthSpaceId}
              AND j."Status" = 'draft'
              AND EXISTS (SELECT 1 FROM "ReceiptScanSources" s WHERE s."ReceiptScanJobId" = j."Id")
            """, ct);
        // Wake the worker instead of letting it discover this on its next poll: that is what allows the
        // idle loop to back off without making an upload wait for it.
        if (affected == 1) signal?.Notify();
        return affected == 1;
    }

    public async Task<bool> RetryAsync(Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs" AS j
            SET "Status" = 'queued', "Stage" = 'queued', "Engine" = NULL, "ReaderVersion" = NULL, "Reason" = NULL, "Lane" = NULL, "Error" = NULL,
                "StartedAt" = NULL, "CompletedAt" = NULL, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE j."Id" = {jobId} AND j."UserId" = {userId} AND j."FullWorthSpaceId" = {fullWorthSpaceId}
              AND j."Status" = 'error'
              AND EXISTS (SELECT 1 FROM "ReceiptScanSources" s WHERE s."ReceiptScanJobId" = j."Id")
            """, ct);
        if (affected == 1) signal?.Notify();
        return affected == 1;
    }

    /// <summary>
    /// Hands a job the fast lane could not read locally to the AI lane: it is queued again - at its old
    /// place, the claim order is by creation time - and remembers why.
    /// </summary>
    public async Task<int> HandToAiLaneAsync(Guid jobId, string reason, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs"
            SET "Status" = 'queued', "Stage" = 'queued', "Lane" = {ReceiptScanLanes.Ai}, "Reason" = {reason},
                "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {jobId} AND "Status" = 'processing'
            """, ct);
        if (affected == 1) signal?.Notify();
        return affected;
    }

    /// <summary>
    /// Claims exactly one queued job of one lane across all FullWorth Spaces. <paramref name="lane"/> null is
    /// the fast lane's queue: jobs nobody has looked at yet. PostgreSQL executes the CTE + UPDATE
    /// as one statement/transaction; SKIP LOCKED keeps this safe if multiple backend replicas exist.
    /// Draft jobs are intentionally invisible until the user explicitly starts the complete scan set.
    /// </summary>
    public async Task<ReceiptScanJobRow?> ClaimNextAsync(string? lane, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                WITH next_job AS (
                    SELECT "Id"
                    FROM "ReceiptScanJobs"
                    WHERE "Status" = 'queued' AND "Lane" IS NOT DISTINCT FROM CAST(@lane AS text)
                    ORDER BY "CreatedAt", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                UPDATE "ReceiptScanJobs" AS j
                SET "Status" = 'processing',
                    "Stage" = 'preparing',
                    "Attempts" = j."Attempts" + 1,
                    "StartedAt" = COALESCE(j."StartedAt", NOW()),
                    "UpdatedAt" = NOW(),
                    "Error" = NULL
                FROM next_job
                WHERE j."Id" = next_job."Id"
                RETURNING j."Id", j."FullWorthSpaceId", j."UserId", j."PurchaseId", j."FileName", j."ContentType",
                          j."Status", j."Stage", j."Engine", j."Error", j."WarningsJson", j."Attempts", j."CreatedAt",
                          j."StartedAt", j."CompletedAt", j."UpdatedAt";
                """;

            var parameter = command.CreateParameter();
            parameter.ParameterName = "lane";
            parameter.Value = (object?)lane ?? DBNull.Value;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new ReceiptScanJobRow
            {
                Id = reader.GetGuid(0),
                FullWorthSpaceId = reader.GetGuid(1),
                UserId = reader.GetGuid(2),
                PurchaseId = reader.GetGuid(3),
                FileName = reader.GetString(4),
                ContentType = reader.GetString(5),
                Status = reader.GetString(6),
                Stage = reader.GetString(7),
                Engine = reader.IsDBNull(8) ? null : reader.GetString(8),
                Error = reader.IsDBNull(9) ? null : reader.GetString(9),
                WarningsJson = reader.IsDBNull(10) ? null : reader.GetString(10),
                Attempts = reader.GetInt32(11),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(12),
                StartedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                CompletedAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(15)
            };
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public Task<int> SetStageAsync(Guid jobId, string stage, string? engine, CancellationToken ct, string? reason = null)
    {
        var safeStage = stage.Length <= 32 ? stage : stage[..32];
        var safeEngine = engine is null ? null : (engine.Length <= 32 ? engine : engine[..32]);
        var now = DateTimeOffset.UtcNow;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs"
            SET "Stage" = {safeStage}, "Engine" = COALESCE({safeEngine}, "Engine"),
                "Reason" = COALESCE({reason}, "Reason"), "UpdatedAt" = {now}
            WHERE "Id" = {jobId} AND "Status" = 'processing'
            """, ct);
    }

    public Task<int> CompleteAsync(Guid jobId, string engine, string readerVersion, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs"
            SET "Status" = 'done', "Stage" = 'done', "Engine" = {engine}, "ReaderVersion" = {readerVersion},
                "Error" = NULL, "CompletedAt" = {now}, "UpdatedAt" = {now}
            WHERE "Id" = {jobId}
            """, ct);
    }

    public Task<int> FailAsync(Guid jobId, string error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var safeError = string.IsNullOrWhiteSpace(error) ? "Receipt scan failed." : error.Trim();
        if (safeError.Length > 2000) safeError = safeError[..2000];
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs"
            SET "Status" = 'error', "Stage" = 'error', "Error" = {safeError},
                "CompletedAt" = {now}, "UpdatedAt" = {now}
            WHERE "Id" = {jobId}
            """, ct);
    }

    public Task<int> RequeueStaleAsync(DateTimeOffset staleBefore, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs"
            SET "Status" = 'queued', "Stage" = 'queued', "Engine" = NULL, "ReaderVersion" = NULL, "Reason" = NULL, "Lane" = NULL,
                "StartedAt" = NULL, "CompletedAt" = NULL, "UpdatedAt" = {now},
                "Error" = NULL
            WHERE "Status" = 'processing' AND "UpdatedAt" < {staleBefore}
            """, ct);
    }

    public Task<int> SetPurchaseStatusAsync(Guid purchaseId, string status, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Purchases" SET "Status" = {status}, "UpdatedAt" = {now} WHERE "Id" = {purchaseId}
            """, ct);
    }
}
