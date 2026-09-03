using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

public sealed class AmazonSqlStore(FullWorthDbContext db)
{
    public Task<AmazonConnection?> GetConnectionAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Database.SqlQuery<AmazonConnection>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "Marketplace", "EncryptedStorageState", "Status",
                   "LastSyncAt", "LastSuccessfulSyncAt", "LastError", "CreatedAt", "UpdatedAt"
            FROM "AmazonConnections"
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "Marketplace" = {"amazon.de"}
            """).SingleOrDefaultAsync(ct);

    public async Task UpsertConnectionAsync(Guid userId, Guid fullWorthSpaceId, string encryptedStorageState, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AmazonConnections"
                ("Id", "FullWorthSpaceId", "UserId", "Marketplace", "EncryptedStorageState", "Status", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {fullWorthSpaceId}, {userId}, {"amazon.de"}, {encryptedStorageState}, {"connected"}, {now}, {now})
            ON CONFLICT ("FullWorthSpaceId", "UserId", "Marketplace") DO UPDATE SET
                "EncryptedStorageState" = EXCLUDED."EncryptedStorageState",
                "Status" = {"connected"},
                "LastError" = NULL,
                "UpdatedAt" = {now}
            """, ct);
    }

    public Task<int> DeleteConnectionAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "AmazonConnections"
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "Marketplace" = {"amazon.de"}
            """, ct);

    public Task<int> MarkSyncStartedAsync(Guid userId, Guid fullWorthSpaceId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AmazonConnections" SET "LastSyncAt" = {now}, "UpdatedAt" = {now}
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "Marketplace" = {"amazon.de"}
            """, ct);

    public Task<int> MarkSyncSuccessAsync(Guid userId, Guid fullWorthSpaceId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AmazonConnections" SET "Status" = {"connected"}, "LastSuccessfulSyncAt" = {now}, "LastError" = NULL, "UpdatedAt" = {now}
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "Marketplace" = {"amazon.de"}
            """, ct);

    public Task<int> MarkSyncFailureAsync(Guid userId, Guid fullWorthSpaceId, string status, string error, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AmazonConnections" SET "Status" = {status}, "LastError" = {error}, "UpdatedAt" = {now}
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "Marketplace" = {"amazon.de"}
            """, ct);

    public Task<List<AmazonConnectionDueRow>> ListDueConnectionsAsync(DateTimeOffset dueBefore, CancellationToken ct) =>
        db.Database.SqlQuery<AmazonConnectionDueRow>($"""
            SELECT "UserId", "FullWorthSpaceId"
            FROM "AmazonConnections"
            WHERE "Status" IN ('connected', 'error')
              AND COALESCE("LastSyncAt", "LastSuccessfulSyncAt", "CreatedAt") < {dueBefore}
            ORDER BY COALESCE("LastSyncAt", "LastSuccessfulSyncAt", "CreatedAt")
            LIMIT 20
            """).ToListAsync(ct);

    public Task<int> UpsertOrderMetadataAsync(Guid purchaseId, string? externalStatus, decimal detectedNonBankPaymentAmount, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AmazonOrderMetadata"
                ("PurchaseId", "ExternalStatus", "NonBankPaymentAmount", "NonBankPaymentSource", "UpdatedAt")
            VALUES ({purchaseId}, {externalStatus}, {Math.Max(0m, detectedNonBankPaymentAmount)}, {"amazon"}, {now})
            ON CONFLICT ("PurchaseId") DO UPDATE SET
                "ExternalStatus" = EXCLUDED."ExternalStatus",
                "NonBankPaymentAmount" = CASE
                    WHEN "AmazonOrderMetadata"."NonBankPaymentSource" = 'manual' THEN "AmazonOrderMetadata"."NonBankPaymentAmount"
                    ELSE EXCLUDED."NonBankPaymentAmount" END,
                "NonBankPaymentSource" = CASE
                    WHEN "AmazonOrderMetadata"."NonBankPaymentSource" = 'manual' THEN 'manual'
                    ELSE EXCLUDED."NonBankPaymentSource" END,
                "UpdatedAt" = {now}
            """, ct);

    public Task<AmazonOrderMetadata?> GetOrderMetadataAsync(Guid purchaseId, CancellationToken ct) =>
        db.Database.SqlQuery<AmazonOrderMetadata>($"""
            SELECT "PurchaseId", "ExternalStatus", "NonBankPaymentAmount", "NonBankPaymentSource", "UpdatedAt"
            FROM "AmazonOrderMetadata" WHERE "PurchaseId" = {purchaseId}
            """).SingleOrDefaultAsync(ct);

    public Task<int> SetManualNonBankPaymentAsync(Guid purchaseId, decimal amount, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AmazonOrderMetadata"
                ("PurchaseId", "ExternalStatus", "NonBankPaymentAmount", "NonBankPaymentSource", "UpdatedAt")
            VALUES ({purchaseId}, NULL, {amount}, {"manual"}, {now})
            ON CONFLICT ("PurchaseId") DO UPDATE SET
                "NonBankPaymentAmount" = EXCLUDED."NonBankPaymentAmount",
                "NonBankPaymentSource" = {"manual"},
                "UpdatedAt" = {now}
            """, ct);

    public async Task UpsertRefundsAsync(Guid purchaseId, IReadOnlyList<AmazonRefundSnapshot> refunds, CancellationToken ct)
    {
        foreach (var refund in refunds)
        {
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PurchaseRefunds"
                    ("Id", "PurchaseId", "ExternalRefundId", "RefundDate", "Amount", "Currency", "Status", "Description", "CreatedAt", "UpdatedAt")
                VALUES ({id}, {purchaseId}, {refund.ExternalRefundId}, {refund.RefundDate}, {refund.Amount}, {refund.Currency}, {refund.Status}, {refund.Description}, {now}, {now})
                ON CONFLICT ("PurchaseId", "ExternalRefundId") DO UPDATE SET
                    "RefundDate" = EXCLUDED."RefundDate", "Amount" = EXCLUDED."Amount", "Currency" = EXCLUDED."Currency",
                    "Status" = EXCLUDED."Status", "Description" = EXCLUDED."Description", "UpdatedAt" = {now}
                """, ct);
        }
    }

    public Task<List<PurchaseRefund>> ListRefundsAsync(Guid purchaseId, CancellationToken ct) =>
        db.Database.SqlQuery<PurchaseRefund>($"""
            SELECT "Id", "PurchaseId", "ExternalRefundId", "RefundDate", "Amount", "Currency", "Status", "Description",
                   "TransactionId", "MatchConfidence", "CreatedAt", "UpdatedAt"
            FROM "PurchaseRefunds" WHERE "PurchaseId" = {purchaseId}
            ORDER BY "RefundDate" DESC, "Id"
            """).ToListAsync(ct);

    public Task<int> SetRefundTransactionAsync(Guid refundId, Guid transactionId, decimal confidence, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PurchaseRefunds" SET "TransactionId" = {transactionId}, "MatchConfidence" = {confidence}, "UpdatedAt" = {now}
            WHERE "Id" = {refundId} AND "TransactionId" IS NULL
            """, ct);

    public Task<int> SetRefundTransactionManualAsync(Guid purchaseId, Guid refundId, Guid transactionId, decimal? confidence, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PurchaseRefunds" SET "TransactionId" = {transactionId}, "MatchConfidence" = {confidence}, "UpdatedAt" = {now}
            WHERE "Id" = {refundId} AND "PurchaseId" = {purchaseId}
            """, ct);

    public Task<int> ClearRefundTransactionAsync(Guid purchaseId, Guid refundId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PurchaseRefunds" SET "TransactionId" = NULL, "MatchConfidence" = NULL, "UpdatedAt" = {now}
            WHERE "Id" = {refundId} AND "PurchaseId" = {purchaseId}
            """, ct);
    }

    // Amazon and normal receipt linking share PurchasePaymentLinks. The Amazon-facing DTO keeps its
    // historic AllocatedAmount/MatchConfidence/Source names so the browser/API contract is unchanged.
    public Task<List<PurchaseTransactionLink>> ListPaymentLinksAsync(Guid purchaseId, CancellationToken ct) =>
        db.Database.SqlQuery<PurchaseTransactionLink>($"""
            SELECT "PurchaseId", "TransactionId", "Amount" AS "AllocatedAmount", "Confidence" AS "MatchConfidence",
                   "LinkSource" AS "Source", "CreatedAt"
            FROM "PurchasePaymentLinks" WHERE "PurchaseId" = {purchaseId} ORDER BY "CreatedAt", "TransactionId"
            """).ToListAsync(ct);

    public Task<List<PurchaseTransactionLink>> ListAllPaymentLinksAsync(CancellationToken ct) =>
        db.Database.SqlQuery<PurchaseTransactionLink>($"""
            SELECT "PurchaseId", "TransactionId", "Amount" AS "AllocatedAmount", "Confidence" AS "MatchConfidence",
                   "LinkSource" AS "Source", "CreatedAt"
            FROM "PurchasePaymentLinks"
            """).ToListAsync(ct);

    public Task<int> UpsertPaymentLinkAsync(Guid purchaseId, Guid transactionId, decimal allocatedAmount, decimal? confidence, string source, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PurchasePaymentLinks"
                ("Id", "FullWorthSpaceId", "PurchaseId", "TransactionId", "Amount", "Currency", "LinkSource", "Confidence", "CreatedByUserId", "CreatedAt", "UpdatedAt")
            SELECT {id}, p."FullWorthSpaceId", p."Id", {transactionId}, {allocatedAmount}, p."Currency", {source}, {confidence}, p."CreatedByUserId", {now}, {now}
            FROM "Purchases" p
            WHERE p."Id" = {purchaseId}
            ON CONFLICT ("PurchaseId", "TransactionId") DO UPDATE SET
                "Amount" = EXCLUDED."Amount",
                "Currency" = EXCLUDED."Currency",
                "Confidence" = EXCLUDED."Confidence",
                "LinkSource" = EXCLUDED."LinkSource",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);
    }

    public Task<int> DeletePaymentLinkAsync(Guid purchaseId, Guid transactionId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "PurchasePaymentLinks" WHERE "PurchaseId" = {purchaseId} AND "TransactionId" = {transactionId}
            """, ct);

    public Task<List<Guid>> ListAllLinkedPaymentTransactionIdsAsync(CancellationToken ct) =>
        db.Database.SqlQuery<Guid>($"SELECT DISTINCT \"TransactionId\" AS \"Value\" FROM \"PurchasePaymentLinks\"").ToListAsync(ct);

    public Task<List<Guid>> ListAllLinkedRefundTransactionIdsAsync(CancellationToken ct) =>
        db.Database.SqlQuery<Guid>($"SELECT \"TransactionId\" AS \"Value\" FROM \"PurchaseRefunds\" WHERE \"TransactionId\" IS NOT NULL").ToListAsync(ct);
}

public sealed class AmazonConnectionDueRow
{
    public Guid UserId { get; set; }
    public Guid FullWorthSpaceId { get; set; }
}
