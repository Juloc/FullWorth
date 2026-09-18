using FullWorth.Backend.Data;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>The two import runs that can create a purchase and therefore can roll one back (#141).</summary>
internal static class PurchaseImportSources
{
    internal const string Receipt = "receipt";
    internal const string Amazon = "amazon";
}

/// <summary>
/// Records which purchases one import run created, so the run can be undone. Both purchase-creating
/// imports - a receipt import batch and an Amazon sync run - write here; without it the only trace of a
/// created purchase was its Source column, which says HOW it was created, not WHICH run is responsible
/// for it, so nothing could tell two receipt batches or two Amazon syncs apart.
///
/// Deliberately not the same thing as "ReceiptImportItems.PurchaseId": that column is workflow
/// bookkeeping (which item currently points at which purchase) and may be reassigned. This table is an
/// immutable audit trail of what a run created, and a purchase belongs to exactly one run for its
/// lifetime - see the UNIQUE constraint on "PurchaseId" alone.
/// </summary>
internal static class PurchaseImportProvenance
{
    // Every foreign key that points at "Purchases" or at "PurchaseItems" (a purchase's own child a
    // rollback cannot see directly), minus this table's own link. A purchase the user has since worked
    // on is KEPT, not deleted: the CASCADE ones would silently take the user's work with them (a bank
    // link, a return, a difference the user accepted) and the RESTRICT/SET NULL ones would either abort
    // the rollback or quietly detach real data. PurchaseImportProvenanceGuardTests compares this list
    // against the live schema, so a new table referencing a purchase cannot quietly fall outside it.
    //
    // Four tables are deliberately NOT guarded here, because they are the purchase's own disposable
    // content and already cascade away with it today (PurchaseWorkspaceService.DeletePurchaseAsync):
    // "PurchaseItems", "PurchaseDiscounts", "PurchaseDocuments", "PurchaseTagLinks". Three more join
    // them for the same reason even though they were not part of that older path - they describe how a
    // purchase's OWN content was produced, not a decision the user made about it, and have no meaning
    // once the purchase is gone: "AmazonOrderMetadata" (the purchase's own payment-source breakdown),
    // "ReceiptScanJobs" and "ReceiptScanItemSources" (the OCR run that produced it).
    internal const string DeleteImportedPurchasesSql = """
DELETE FROM "Purchases" p
USING "PurchaseImportLinks" l
WHERE l."ImportBatchId"=@batch AND l."ImportSource"=@source AND l."PurchaseId"=p."Id"
  AND NOT EXISTS (SELECT 1 FROM "PurchasePaymentLinks" x WHERE x."PurchaseId"=p."Id")
  AND NOT EXISTS (SELECT 1 FROM "PurchaseAllocationLinks" x WHERE x."PurchaseId"=p."Id")
  AND NOT EXISTS (SELECT 1 FROM "PurchaseItemReturns" x
                  WHERE x."PurchaseItemId" IN (SELECT "Id" FROM "PurchaseItems" WHERE "PurchaseId"=p."Id"))
  AND NOT EXISTS (SELECT 1 FROM "TransactionAllocations" x
                  WHERE x."PurchaseItemId" IN (SELECT "Id" FROM "PurchaseItems" WHERE "PurchaseId"=p."Id"))
  AND NOT EXISTS (SELECT 1 FROM "PurchaseDifferenceAcceptances" x WHERE x."PurchaseId"=p."Id")
  AND NOT EXISTS (SELECT 1 FROM "PurchaseReconciliationConfirmations" x WHERE x."PurchaseId"=p."Id")
  AND NOT EXISTS (SELECT 1 FROM "PurchaseRefunds" x WHERE x."PurchaseId"=p."Id")
  AND NOT EXISTS (SELECT 1 FROM "SpendingReviews" x WHERE x."PurchaseId"=p."Id")
RETURNING p."Id", p."ReceiptImagePath"
""";

    internal static async Task LinkAsync(
        FullWorthDbContext db, Guid importBatchId, string importSource, IReadOnlyCollection<Guid> purchaseIds, CancellationToken ct)
    {
        if (purchaseIds.Count == 0) return;
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var purchaseId in purchaseIds)
        {
            await using var command = RawSql.Command(connection, """
INSERT INTO "PurchaseImportLinks" ("ImportBatchId","ImportSource","PurchaseId","CreatedAt")
VALUES (@batch,@source,@purchase,@now)
ON CONFLICT ("PurchaseId") DO NOTHING
""", ("@batch", importBatchId), ("@source", importSource), ("@purchase", purchaseId), ("@now", now));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    internal static async Task<int> LinkCountAsync(FullWorthDbContext db, Guid importBatchId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT count(*) FROM \"PurchaseImportLinks\" WHERE \"ImportBatchId\"=@batch", ("@batch", importBatchId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The purchases one import run is on record for having created.</summary>
    internal static async Task<IReadOnlyList<Guid>> LinkedPurchaseIdsAsync(FullWorthDbContext db, Guid importBatchId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"PurchaseId\" FROM \"PurchaseImportLinks\" WHERE \"ImportBatchId\"=@batch", ("@batch", importBatchId));
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(RawSql.Guid(reader, "PurchaseId"));
        return ids;
    }

    /// <summary>
    /// Stored document paths for the given purchases, paired with their purchase id so the caller can
    /// filter to whichever ones <see cref="DeleteAsync"/> actually removed. Read this BEFORE
    /// <see cref="DeleteAsync"/> - the guarded delete cascades "PurchaseDocuments" away with any purchase
    /// it removes, so this is the only chance to learn which files belonged to it.
    /// </summary>
    internal static async Task<IReadOnlyList<(Guid PurchaseId, string StoragePath)>> DocumentPathsAsync(
        FullWorthDbContext db, IReadOnlyCollection<Guid> purchaseIds, CancellationToken ct)
    {
        if (purchaseIds.Count == 0) return [];
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"PurchaseId\",\"StoragePath\" FROM \"PurchaseDocuments\" WHERE \"PurchaseId\" = ANY(@ids)",
            ("@ids", purchaseIds.ToArray()));
        var paths = new List<(Guid, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var path = RawSql.NullableString(reader, "StoragePath");
            if (!string.IsNullOrWhiteSpace(path)) paths.Add((RawSql.Guid(reader, "PurchaseId"), path));
        }
        return paths;
    }

    /// <summary>
    /// Runs the guarded delete for one import run and returns exactly the purchases it actually removed,
    /// each with its legacy <c>ReceiptImagePath</c> (documents were already read via
    /// <see cref="DocumentPathsAsync"/> - the cascade removes those rows as part of this statement).
    /// </summary>
    internal static async Task<IReadOnlyList<(Guid PurchaseId, string? ReceiptImagePath)>> DeleteAsync(
        FullWorthDbContext db, Guid importBatchId, string importSource, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, DeleteImportedPurchasesSql,
            ("@batch", importBatchId), ("@source", importSource));
        var removed = new List<(Guid, string?)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            removed.Add((RawSql.Guid(reader, "Id"), RawSql.NullableString(reader, "ReceiptImagePath")));
        return removed;
    }
}
