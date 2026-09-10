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
        // Raw SQL on purpose: the row has to look the way it looked at PreviousMigration, and the CLR
        // entity has columns that only later migrations add.
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Assets"
                ("Id", "FullWorthSpaceId", "Name", "Kind", "CurrentValue", "Currency", "ValuedAt",
                 "AnnualGrowthRate", "IncludeInNetWorth", "Notes", "CreatedAt", "UpdatedAt")
            VALUES
                ({assetId}, {FullWorthSpaceDefaults.LegacyId}, {"Legacy cash-like asset"}, {"cash"},
                 {1_234.56m}, {"eur"}, {new DateOnly(2026, 8, 15)}, NULL, TRUE, NULL, {now}, {now});
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var migrated = await db.Assets.SingleAsync(x => x.Id == assetId);
        Assert.Equal("other", migrated.Kind);
        Assert.Equal("EUR", migrated.Currency);
        // A date this far from the row's last-touched day cannot be a CURRENT_DATE stamp, so it was
        // stated and survives as the appraisal date.
        Assert.Equal(new DateOnly(2026, 8, 15), migrated.ValuedAt);
        Assert.Equal(now, migrated.ValueRecordedAt, TimeSpan.FromMinutes(1));

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Amount", "Currency", "ValuedAt", "Method", "IsCurrent", "IsAccepted",
                   "InputSummaryJson"->>'legacyKind', "ValuedAtIsStated"
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
        Assert.True(reader.GetBoolean(7));
        Assert.False(await reader.ReadAsync());
    }

    // The stamp the old prepare trigger wrote is not an appraisal date, and promoting it to one is what
    // made real appraisals look stale. It must not survive as a date the asset claims to be valued at.
    [Fact]
    public async Task SyntheticValuedAtStampIsDroppedAndNotPromotedToAnAppraisalDate()
    {
        var options = new DbContextOptionsBuilder<FullWorthDbContext>()
            .UseNpgsql(CreateConnectionString())
            .ReplaceService<IModelCustomizer, CoachModelCustomizer>()
            .Options;

        await using var db = new FullWorthDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260910233000_OccupationalPension");

        // Exactly what the old trigger produced: an asset saved without a date, stamped with the day it
        // was saved, mirrored into a "current" valuation carrying that same stamp.
        var stampedId = Guid.NewGuid();
        var statedId = Guid.NewGuid();
        var touched = DateTimeOffset.UtcNow;
        var stamp = DateOnly.FromDateTime(touched.UtcDateTime);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Assets"
                ("Id", "FullWorthSpaceId", "Name", "Kind", "CurrentValue", "Currency", "ValuedAt",
                 "AnnualGrowthRate", "IncludeInNetWorth", "Notes", "CreatedAt", "UpdatedAt")
            VALUES
                ({stampedId}, {FullWorthSpaceDefaults.LegacyId}, {"Stamped house"}, {"real_estate"},
                 {400_000m}, {"EUR"}, {stamp}, NULL, TRUE, NULL, {touched}, {touched}),
                ({statedId}, {FullWorthSpaceDefaults.LegacyId}, {"Appraised house"}, {"real_estate"},
                 {500_000m}, {"EUR"}, {new DateOnly(2026, 5, 4)}, NULL, TRUE, NULL, {touched}, {touched});
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var stamped = await db.Assets.SingleAsync(x => x.Id == stampedId);
        Assert.Null(stamped.ValuedAt);
        Assert.Equal(400_000m, stamped.CurrentValue);
        Assert.Equal("EUR", stamped.Currency);
        Assert.Equal(touched, stamped.ValueRecordedAt, TimeSpan.FromMinutes(1));

        var stated = await db.Assets.SingleAsync(x => x.Id == statedId);
        Assert.Equal(new DateOnly(2026, 5, 4), stated.ValuedAt);
        Assert.Equal(500_000m, stated.CurrentValue);

        // The mirrored valuations keep their sortable date; only the stamped one denies it was stated.
        Assert.False(await ValuedAtIsStatedAsync(db, stampedId));
        Assert.Equal(stamp, await CurrentValuationDateAsync(db, stampedId));
        Assert.True(await ValuedAtIsStatedAsync(db, statedId));
        Assert.Equal(new DateOnly(2026, 5, 4), await CurrentValuationDateAsync(db, statedId));
    }

    private static async Task<bool> ValuedAtIsStatedAsync(FullWorthDbContext db, Guid assetId) =>
        await ScalarAsync<bool>(db, """
            SELECT "ValuedAtIsStated" FROM "AssetValuations"
            WHERE "AssetId"=@asset AND "IsCurrent" = TRUE;
            """, assetId);

    private static async Task<DateOnly> CurrentValuationDateAsync(FullWorthDbContext db, Guid assetId) =>
        await ScalarAsync<DateOnly>(db, """
            SELECT "ValuedAt" FROM "AssetValuations"
            WHERE "AssetId"=@asset AND "IsCurrent" = TRUE;
            """, assetId);

    private static async Task<T> ScalarAsync<T>(FullWorthDbContext db, string sql, Guid assetId)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@asset";
        parameter.Value = assetId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<T>(0);
    }

    private static string CreateConnectionString()
    {
        var server = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException("FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server.");
        return $"{server.TrimEnd(';')};Database=fullworth_asset_valuation_{Guid.NewGuid():N};Maximum Pool Size=50;Minimum Pool Size=0";
    }
}
