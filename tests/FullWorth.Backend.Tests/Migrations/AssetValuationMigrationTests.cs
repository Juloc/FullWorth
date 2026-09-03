using System.Data;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Coach;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FullWorth.Backend.Tests.Migrations;

public sealed class AssetValuationMigrationTests
{
    private const string PreviousMigration = "20260831140000_UnifyLegacyPurchaseProductSchema";

    [Fact]
    public async Task ExistingAssetGetsLegacyValuationAndUnknownKindIsPreservedThenNormalized()
    {
        var options = new DbContextOptionsBuilder<FullWorthDbContext>()
            .UseNpgsql(CreateConnectionString())
            .ReplaceService<IModelCustomizer, CoachModelCustomizer>()
            .Options;

        await using var db = new FullWorthDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var assetId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = assetId,
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            Name = "Legacy cash-like asset",
            Kind = "cash",
            CurrentValue = 1_234.56m,
            Currency = "eur",
            ValuedAt = new DateOnly(2026, 8, 15),
            IncludeInNetWorth = true
        });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var migrated = await db.Assets.SingleAsync(x => x.Id == assetId);
        Assert.Equal("other", migrated.Kind);
        Assert.Equal("EUR", migrated.Currency);
        Assert.Equal(new DateOnly(2026, 8, 15), migrated.ValuedAt);

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Amount", "Currency", "ValuedAt", "Method", "IsCurrent", "IsAccepted",
                   "InputSummaryJson"->>'legacyKind'
            FROM "AssetValuations"
            WHERE "AssetId"=@asset;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@asset";
        parameter.Value = assetId;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1_234.56m, reader.GetDecimal(0));
        Assert.Equal("EUR", reader.GetString(1));
        Assert.Equal(new DateOnly(2026, 8, 15), reader.GetFieldValue<DateOnly>(2));
        Assert.Equal("legacy", reader.GetString(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.Equal("cash", reader.GetString(6));
        Assert.False(await reader.ReadAsync());
    }

    private static string CreateConnectionString()
    {
        var server = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException("FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server.");
        return $"{server.TrimEnd(';')};Database=fullworth_asset_valuation_{Guid.NewGuid():N};Maximum Pool Size=50;Minimum Pool Size=0";
    }
}
