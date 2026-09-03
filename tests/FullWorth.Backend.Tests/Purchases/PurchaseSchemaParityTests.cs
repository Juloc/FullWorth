using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseSchemaParityTests
{
    [Fact]
    public async Task Migrated_database_contains_final_purchase_indexes_and_integrity_trigger()
    {
        using var factory = new BackendWebApplicationFactory();

        await factory.SeedAsync(async db =>
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                var expected = new[]
                {
                    "IX_TransactionAllocations_PurchaseItemId",
                    "IX_Purchases_MerchantId",
                    "IX_Products_DefaultCategoryId",
                    "IX_ProductAliases_MerchantId",
                    "IX_ProductBarcodes_ProductId",
                    "IX_PurchasePaymentLinks_FullWorthSpaceId",
                    "IX_PurchasePaymentLinks_PurchaseId_TransactionId"
                };

                foreach (var indexName in expected)
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT indexdef FROM pg_indexes WHERE schemaname = current_schema() AND indexname = @name";
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "name";
                    parameter.Value = indexName;
                    command.Parameters.Add(parameter);
                    var indexDefinition = await command.ExecuteScalarAsync();
                    Assert.NotNull(indexDefinition);

                    if (indexName == "IX_PurchasePaymentLinks_PurchaseId_TransactionId")
                        Assert.Contains("CREATE UNIQUE INDEX", Convert.ToString(indexDefinition), StringComparison.OrdinalIgnoreCase);
                }

                await using var triggerCommand = db.Database.GetDbConnection().CreateCommand();
                triggerCommand.CommandText = """
                    SELECT COUNT(*)
                    FROM pg_trigger t
                    JOIN pg_class c ON c.oid = t.tgrelid
                    WHERE c.relname = 'PurchasePaymentLinks'
                      AND t.tgname = 'trg_purchase_payment_allocation_guard'
                      AND NOT t.tgisinternal
                    """;
                Assert.Equal(1L, Convert.ToInt64(await triggerCommand.ExecuteScalarAsync()));
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        });
    }
}
