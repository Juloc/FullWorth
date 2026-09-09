using System.IO;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Rolling back a transaction import deletes rows the user can have worked on since. The delete is
/// guarded by a NOT EXISTS per table that references a transaction - a hand-written list, which is
/// exactly the kind of list that rots: add a table with a CASCADE foreign key to "Transactions" and a
/// rollback would silently take that user's work with it.
///
/// This compares the list against the live schema instead of trusting it.
/// </summary>
public sealed class ImportTransactionProvenanceGuardTests
{
    [Fact]
    public async Task RollbackProtectsEveryTableThatPointsAtATransaction()
    {
        var referencing = new List<string>();
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT DISTINCT c.conrelid::regclass::text
FROM pg_constraint c
WHERE c.contype='f'
  AND c.confrelid=(SELECT oid FROM pg_class WHERE relname='Transactions' AND relkind='r' LIMIT 1)
""";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) referencing.Add(reader.GetString(0).Trim('"'));
        });

        Assert.NotEmpty(referencing);
        var sql = File.ReadAllText(Path.Combine(Root(),
            "src", "FullWorth.Backend", "Modules", "Parity", "ImportTransactionProvenance.cs"));

        foreach (var table in referencing)
        {
            // Its own link rows are removed by the cascade, so they must not block the delete.
            if (table == "ImportTransactionLinks") continue;
            Assert.True(
                sql.Contains($"\"{table}\"", StringComparison.Ordinal),
                $"\"{table}\" has a foreign key to \"Transactions\" but the import rollback does not mention it. "
                + "Decide deliberately: either it blocks the delete (add a NOT EXISTS) or it may be cascaded away.");
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
