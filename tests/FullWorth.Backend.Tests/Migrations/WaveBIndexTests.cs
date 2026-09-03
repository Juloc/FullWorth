using System.Data;
using System.Net;
using FullWorth.Backend.Data;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Migrations;

public sealed class WaveBIndexTests
{
    [Fact]
    public async Task RequiredWaveBIndexesExistInPostgres()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var indexes = await GetPublicIndexesAsync(db);

        var required = new[]
        {
            "IX_Users_EmailNormalized",
            "IX_FullWorthSpaceMembers_FullWorthSpaceId_Role",
            "IX_FullWorthSpaceMembers_UserId_FullWorthSpaceId",
            "IX_AccountOwners_UserId_AccountId",
            "IX_BankConnections_FullWorthSpaceId",
            "IX_Accounts_FullWorthSpaceId",
            "IX_Accounts_FullWorthSpaceId_Provider_IdentificationHash",
            "IX_Categories_FullWorthSpaceId",
            "IX_Categories_FullWorthSpaceId_Key",
            "IX_CategorizationRules_FullWorthSpaceId",
            "IX_Contracts_FullWorthSpaceId",
            "IX_Budgets_FullWorthSpaceId",
            "IX_Assets_FullWorthSpaceId",
            "IX_Liabilities_FullWorthSpaceId",
            "IX_NetWorthSnapshots_FullWorthSpaceId_UserId",
            "IX_Purchases_FullWorthSpaceId"
        };

        foreach (var expected in required)
            Assert.Contains(expected, indexes);
    }

    private static async Task<HashSet<string>> GetPublicIndexesAsync(FullWorthDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = 'public';";
            await using var reader = await command.ExecuteReaderAsync();
            var indexes = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
            return indexes;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }
}
