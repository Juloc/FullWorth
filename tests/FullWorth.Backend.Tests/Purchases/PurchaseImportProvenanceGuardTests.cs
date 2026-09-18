using System.IO;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// Rolling back a receipt-import batch or an Amazon sync run deletes purchases the user can have worked
/// on since. The delete is guarded by a NOT EXISTS per table that references a purchase (or, one level
/// down, a purchase item) - a hand-written list, which is exactly the kind of list that rots: add a
/// table with a foreign key to "Purchases" or "PurchaseItems" and a rollback would silently take that
/// user's work with it.
///
/// This compares the list against the live schema instead of trusting it. Modelled on
/// ImportTransactionProvenanceGuardTests, the equivalent test for the transaction-import rollback.
/// </summary>
public sealed class PurchaseImportProvenanceGuardTests
{
    [Fact]
    public async Task RollbackAccountsForEveryTableThatPointsAtAPurchaseOrPurchaseItem()
    {
        var referencing = new List<string>();
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();

            async Task CollectAsync(string principalTable)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
SELECT DISTINCT c.conrelid::regclass::text
FROM pg_constraint c
WHERE c.contype='f'
  AND c.confrelid=(SELECT oid FROM pg_class WHERE relname=@table AND relkind='r' LIMIT 1)
""";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "table";
                parameter.Value = principalTable;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) referencing.Add(reader.GetString(0).Trim('"'));
            }

            await CollectAsync("Purchases");
            await CollectAsync("PurchaseItems");
        });

        Assert.NotEmpty(referencing);
        var quelle = Directory.EnumerateFiles(
                Path.Combine(Root(), "src", "FullWorth.Backend"),
                "PurchaseImportProvenance.cs", SearchOption.AllDirectories)
            .SingleOrDefault()
            ?? throw new FileNotFoundException(
                "PurchaseImportProvenance.cs ist unter src/FullWorth.Backend nicht (oder mehrfach) zu finden.");
        var sql = File.ReadAllText(quelle);

        foreach (var table in referencing.Distinct())
        {
            // Its own link rows are removed by the cascade, so they must not block the delete.
            if (table == "PurchaseImportLinks") continue;
            Assert.True(
                sql.Contains($"\"{table}\"", StringComparison.Ordinal),
                $"\"{table}\" has a foreign key to \"Purchases\" or \"PurchaseItems\" but the import rollback does not "
                + "mention it. Decide deliberately: either it blocks the delete (add a NOT EXISTS) or it cascades "
                + "away with the purchase, and either way it belongs in the comment or the SQL.");
        }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
