using FullWorth.Backend.Data;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>
/// Records which transactions an import job created, so the job can be undone. Both transaction
/// import paths (the generic upload and the column-mapped upload) write here; without it the only
/// trace of a commit is the candidate's DuplicateStatus, which says that something was imported but
/// not what, and an ExternalKey prefix, which a later edit is free to change.
/// </summary>
internal static class ImportTransactionProvenance
{
    // Every foreign key that points at "Transactions", minus this table's own link. A transaction the
    // user has since worked on is KEPT, not deleted: the CASCADE ones would silently take the user's
    // work with them (a split, a tag, a contract link, a spending review) and the RESTRICT ones would
    // abort the whole rollback. ImportTransactionProvenanceGuardTests compares this list against the
    // live schema, so a new table referencing transactions cannot quietly fall outside it.
    internal const string DeleteImportedTransactionsSql = """
DELETE FROM "Transactions" t
USING "ImportTransactionLinks" l
WHERE l."ImportJobId"=@job AND l."TransactionId"=t."Id"
  AND EXISTS (SELECT 1 FROM "Accounts" a WHERE a."Id"=t."AccountId" AND a."FullWorthSpaceId"=@space)
  AND NOT EXISTS (SELECT 1 FROM "AssetCashflowEntries" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "ContractTransactionLinks" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "PriceChangeSuggestions" x WHERE x."EvidenceTransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "PurchaseItemReturns" x WHERE x."RefundTransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "PurchasePaymentLinks" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "PurchaseRefunds" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "Purchases" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "ReceivablePayments" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "RefundSuggestionDismissals" x
                  WHERE x."OriginalTransactionId"=t."Id" OR x."RefundTransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "SpendingReviews" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "TransactionAllocations" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "TransactionReviewStates" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "TransactionTags" x WHERE x."TransactionId"=t."Id")
  AND NOT EXISTS (SELECT 1 FROM "Transactions" x WHERE x."RefundOfTransactionId"=t."Id")
""";

    internal static async Task LinkAsync(
        FullWorthDbContext db, Guid jobId, IReadOnlyCollection<Guid> transactionIds, CancellationToken ct)
    {
        if (transactionIds.Count == 0) return;
        var connection = await ParitySql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var transactionId in transactionIds)
        {
            await using var command = ParitySql.Command(connection, """
INSERT INTO "ImportTransactionLinks" ("ImportJobId","TransactionId","CreatedAt")
VALUES (@job,@transaction,@now)
ON CONFLICT ("TransactionId") DO NOTHING
""", ("@job", jobId), ("@transaction", transactionId), ("@now", now));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    internal static async Task<int> LinkCountAsync(FullWorthDbContext db, Guid jobId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT count(*) FROM \"ImportTransactionLinks\" WHERE \"ImportJobId\"=@job", ("@job", jobId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
