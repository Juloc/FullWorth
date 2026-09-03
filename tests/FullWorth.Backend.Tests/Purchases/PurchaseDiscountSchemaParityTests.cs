using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseDiscountSchemaParityTests
{
    [Fact]
    public async Task Migrated_database_contains_discount_and_allocation_provenance_schema()
    {
        using var factory = new BackendWebApplicationFactory();

        await factory.SeedAsync(async db =>
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                foreach (var table in new[] { "PurchaseDiscounts", "PurchaseAllocationLinks" })
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name";
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "name";
                    parameter.Value = table;
                    command.Parameters.Add(parameter);
                    Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
                }

                var expectedColumns = new[]
                {
                    (Table: "Purchases", Column: "RoundingAmount"),
                    (Table: "PurchaseItems", Column: "OriginalUnitPrice"),
                    (Table: "PurchaseItems", Column: "DiscountLabel")
                };
                foreach (var expected in expectedColumns)
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = """
                        SELECT COUNT(*)
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = @table
                          AND column_name = @column
                        """;
                    var tableParameter = command.CreateParameter();
                    tableParameter.ParameterName = "table";
                    tableParameter.Value = expected.Table;
                    command.Parameters.Add(tableParameter);
                    var columnParameter = command.CreateParameter();
                    columnParameter.ParameterName = "column";
                    columnParameter.Value = expected.Column;
                    command.Parameters.Add(columnParameter);
                    Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
                }

                var expectedIndexes = new[]
                {
                    "IX_PurchaseDiscounts_PurchaseId_CreatedAt",
                    "IX_PurchaseDiscounts_PurchaseItemId",
                    "IX_PurchaseAllocationLinks_TransactionAllocationId",
                    "IX_PurchaseAllocationLinks_PurchaseId",
                    "IX_PurchaseAllocationLinks_PurchaseDiscountId"
                };
                foreach (var index in expectedIndexes)
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT indexdef FROM pg_indexes WHERE schemaname = current_schema() AND indexname = @name";
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "name";
                    parameter.Value = index;
                    command.Parameters.Add(parameter);
                    var definition = Convert.ToString(await command.ExecuteScalarAsync());
                    Assert.False(string.IsNullOrWhiteSpace(definition));
                    if (index == "IX_PurchaseAllocationLinks_TransactionAllocationId")
                        Assert.Contains("CREATE UNIQUE INDEX", definition!, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        });
    }
}
