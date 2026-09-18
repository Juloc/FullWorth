using FullWorth.Backend.Data;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Records which accounts an import job created, for the one import that can create more than one in
/// a single run: Finanzguru groups rows by source account and calls
/// <c>AccountStore.CreateForImportAsync</c> once per group. "ImportJobs.CreatedAccountId" is a single
/// column and can only ever say "this one" - without this table a second or third created account had
/// no trace at all, and a rollback left it behind even though the user asked to undo the whole import.
///
/// The generic CSV/statement import still uses the single column alone; ImportJobStore.RollbackAsync
/// treats a row here exactly the same way, as an additional place an eligible account can be found.
/// </summary>
internal static class ImportJobAccountProvenance
{
    internal static async Task LinkCreatedAccountsAsync(
        FullWorthDbContext db, Guid jobId, IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        if (accountIds.Count == 0) return;
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var accountId in accountIds)
        {
            await using var command = RawSql.Command(connection, """
INSERT INTO "ImportJobCreatedAccounts" ("ImportJobId","AccountId","CreatedAt")
VALUES (@job,@account,@now)
ON CONFLICT ("AccountId") DO NOTHING
""", ("@job", jobId), ("@account", accountId), ("@now", now));
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
