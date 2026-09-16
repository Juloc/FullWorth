using System.Data;
using FullWorth.Backend.Data;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Migrations;

public sealed class ReceiptImportSpaceCascadeTests
{
    [Theory]
    [InlineData("ReceiptImportBatches", "ReceiptImportBatches_FullWorthSpaceId_fkey")]
    [InlineData("ReceiptImportItems", "ReceiptImportItems_FullWorthSpaceId_fkey")]
    public async Task Receipt_import_space_foreign_keys_cascade(string table, string constraint)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT c.confdeltype::text
FROM pg_constraint c
JOIN pg_class t ON t.oid = c.conrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
WHERE n.nspname = 'public'
  AND t.relname = @table
  AND c.conname = @constraint
  AND c.contype = 'f';
""";

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);

        var constraintParameter = command.CreateParameter();
        constraintParameter.ParameterName = "constraint";
        constraintParameter.Value = constraint;
        command.Parameters.Add(constraintParameter);

        var deleteAction = Convert.ToString(await command.ExecuteScalarAsync());
        Assert.Equal("c", deleteAction); // PostgreSQL pg_constraint: c = CASCADE.
    }
}
