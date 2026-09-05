using System.Data;
using System.Net;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Migrations;

public sealed class FinanceMigrationTests
{
    private static readonly string[] ExpectedTables =
    [
        "AccountGroups",
        "AccountOwners",
        "Accounts",
        "Assets",
        "AuditEvents",
        "BalanceSnapshots",
        "BankConnections",
        "EnableBankingProfiles",
        "Budgets",
        "Categories",
        "CategorizationRules",
        "Contracts",
        "DismissedContractCandidates",
        "FullWorthSpaceInvites",
        "FullWorthSpaceMembers",
        "FullWorthSpaces",
        "FxRates",
        "Liabilities",
        "Loans",
        "MerchantAliases",
        "Merchants",
        "NetWorthSnapshots",
        "NotificationDedups",
        "PurchaseItems",
        "Purchases",
        "PriceChangeSuggestions",
        "PushDevices",
        "UserPreferences",
        "Transactions",
        "TransactionAllocations",
        "Users",
        "__EFMigrationsHistory"
    ];

    [Fact]
    public async Task FreshPostgresDatabaseMigratesSeedsAndStartsHealthy()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        // A fully-migrated fresh database must have applied every migration defined in the
        // assembly, in EF's canonical order, with no pending or duplicated entries.
        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(db.Database.GetMigrations().ToArray(), appliedMigrations);

        var tables = await GetPublicTablesAsync(db);
        foreach (var expectedTable in ExpectedTables)
            Assert.Contains(expectedTable, tables);

        Assert.Equal(1, await db.FullWorthSpaces.CountAsync());
        Assert.True(await db.FullWorthSpaces.AnyAsync(x => x.Id == FullWorthSpaceDefaults.LegacyId));
        Assert.Equal(FullWorthSeeder.DefaultCategoryCount, await db.Categories.CountAsync(x => x.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId));
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.AccountOwners.CountAsync());
    }

    [Fact]
    public async Task SecondStartupAgainstCurrentDatabaseRemainsHealthyWithoutDuplicateSeedData()
    {
        string connectionString;

        using (var firstFactory = new BackendWebApplicationFactory())
        {
            connectionString = firstFactory.ConnectionString;
            using var firstClient = firstFactory.CreateClient();
            using var firstResponse = await firstClient.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }

        using var secondFactory = new BackendWebApplicationFactory(connectionString);
        using var secondClient = secondFactory.CreateClient();
        using var secondResponse = await secondClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        await using var scope = secondFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();

        Assert.Equal(db.Database.GetMigrations().ToArray(), appliedMigrations);
        Assert.Equal(FullWorthSeeder.DefaultCategoryCount, await db.Categories.CountAsync(x => x.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId));
    }

    [Fact]
    public async Task SeederIsPerSpaceIdempotentAndDoesNotOverwriteExistingCategoryEdits()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var seeder = scope.ServiceProvider.GetRequiredService<FullWorthSeeder>();

        var secondSpace = new FullWorthSpace { Name = "Second", BaseCurrency = "EUR" };
        db.FullWorthSpaces.Add(secondSpace);
        await db.SaveChangesAsync();

        await seeder.SeedAsync(db, CancellationToken.None);
        Assert.Equal(FullWorthSeeder.DefaultCategoryCount, await db.Categories.CountAsync(x => x.FullWorthSpaceId == secondSpace.Id));

        var housing = await db.Categories.SingleAsync(x => x.FullWorthSpaceId == secondSpace.Id && x.Key == "housing");
        housing.Name = "Custom housing";
        var removedLeaf = await db.Categories.SingleAsync(x => x.FullWorthSpaceId == secondSpace.Id && x.Key == "cash");
        db.Categories.Remove(removedLeaf);
        await db.SaveChangesAsync();

        await seeder.SeedAsync(db, CancellationToken.None);
        await seeder.SeedAsync(db, CancellationToken.None);

        Assert.Equal(FullWorthSeeder.DefaultCategoryCount, await db.Categories.CountAsync(x => x.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId));
        Assert.Equal(FullWorthSeeder.DefaultCategoryCount, await db.Categories.CountAsync(x => x.FullWorthSpaceId == secondSpace.Id));
        Assert.Equal("Custom housing", await db.Categories
            .Where(x => x.FullWorthSpaceId == secondSpace.Id && x.Key == "housing")
            .Select(x => x.Name)
            .SingleAsync());
        Assert.True(await db.Categories.AnyAsync(x => x.FullWorthSpaceId == secondSpace.Id && x.Key == "cash"));
    }

    [Fact]
    public async Task DirectScopeColumnsAreRequiredAndParentDerivedEntitiesHaveNoDuplicateScopeColumn()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        var directTables = new[]
        {
            "BankConnections", "Accounts", "Categories", "CategorizationRules", "Contracts",
            "DismissedContractCandidates",
            "Budgets", "Loans", "Assets", "Liabilities", "NetWorthSnapshots", "Purchases"
        };
        foreach (var table in directTables)
            Assert.Equal("NO", await GetColumnNullableAsync(db, table, "FullWorthSpaceId"));

        foreach (var table in new[] { "BalanceSnapshots", "Transactions", "PurchaseItems" })
            Assert.Null(await GetColumnNullableAsync(db, table, "FullWorthSpaceId"));
    }

    [Fact]
    public async Task ModelSnapshotMatchesCurrentModel()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<HashSet<string>> GetPublicTablesAsync(FullWorthDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';";

            await using var reader = await command.ExecuteReaderAsync();
            var tables = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            return tables;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private static async Task<string?> GetColumnNullableAsync(FullWorthDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table AND column_name = @column;";
            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "table";
            tableParameter.Value = table;
            command.Parameters.Add(tableParameter);
            var columnParameter = command.CreateParameter();
            columnParameter.ParameterName = "column";
            columnParameter.Value = column;
            command.Parameters.Add(columnParameter);
            return await command.ExecuteScalarAsync() as string;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }
}
