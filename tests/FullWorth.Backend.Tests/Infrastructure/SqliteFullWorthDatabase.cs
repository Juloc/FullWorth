using FullWorth.Backend.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Infrastructure;

internal sealed class SqliteFullWorthDatabase : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly DbContextOptions<FullWorthDbContext> options;

    private SqliteFullWorthDatabase(SqliteConnection connection)
    {
        this.connection = connection;
        options = new DbContextOptionsBuilder<FullWorthDbContext>()
            .UseSqlite(connection)
            .Options;
    }

    public static async Task<SqliteFullWorthDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var database = new SqliteFullWorthDatabase(connection);
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        await CreateParityInvestmentTablesAsync(db);
        return database;
    }

    // The investment/parity tables are created by raw-SQL migrations (not mapped EF entities),
    // so EnsureCreated — which builds the schema from the model — never creates them. Services
    // such as InvestmentNetWorthService query these tables via raw SQL, so the SQLite test
    // database must contain them (empty is fine) for those queries to run.
    private static Task CreateParityInvestmentTablesAsync(FullWorthDbContext db) =>
        db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "InvestmentPortfolios" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL,
  "Name" varchar(160) NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "AccountId" uuid NULL,
  "BenchmarkSecurityId" uuid NULL,
  -- Carries the connection id for a provider-synced depot (fints:CONNECTION:DEPOT), which is
  -- how deleting a bank connection finds the depot data it has to remove with it.
  "ProviderName" varchar(160) NULL,
  "IsArchived" integer NOT NULL DEFAULT 0,
  "IncludeInNetWorth" integer NOT NULL DEFAULT 1,
  "CreatedAt" text NOT NULL,
  "UpdatedAt" text NOT NULL
);
CREATE TABLE IF NOT EXISTS "InvestmentTrades" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL,
  "PortfolioId" uuid NOT NULL,
  "SecurityId" uuid NULL,
  "TradeType" varchar(24) NOT NULL,
  "TradeDate" text NOT NULL,
  "Quantity" numeric NULL,
  "Price" numeric NULL,
  "GrossAmount" numeric NULL,
  "Amount" numeric NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Fees" numeric NOT NULL DEFAULT 0,
  "Taxes" numeric NOT NULL DEFAULT 0,
  "WithholdingTax" numeric NOT NULL DEFAULT 0,
  "CreatedAt" text NOT NULL
);
CREATE TABLE IF NOT EXISTS "SecurityPrices" (
  "SecurityId" uuid NOT NULL,
  "PriceDate" text NOT NULL,
  "Price" numeric NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Source" varchar(64) NOT NULL,
  "CreatedAt" text NOT NULL,
  PRIMARY KEY ("SecurityId","PriceDate","Source")
);
CREATE TABLE IF NOT EXISTS "Securities" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL,
  "Name" varchar(240) NOT NULL,
  "Isin" varchar(12) NULL,
  "ProviderKey" varchar(160) NULL
);
-- Only referenced to decide whether a security is still in use somewhere else, so a shared one
-- survives deleting the bank connection that happened to introduce it.
CREATE TABLE IF NOT EXISTS "WatchlistItems" (
  "WatchlistId" uuid NOT NULL,
  "SecurityId" uuid NOT NULL,
  PRIMARY KEY ("WatchlistId","SecurityId")
);
CREATE TABLE IF NOT EXISTS "BenchmarkDefinitions" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NULL,
  "Name" varchar(160) NOT NULL,
  "SecurityId" uuid NULL
);
CREATE TABLE IF NOT EXISTS "InvestmentImportSecurityLinks" (
  "ImportJobId" uuid NOT NULL,
  "SecurityId" uuid NOT NULL,
  PRIMARY KEY ("ImportJobId","SecurityId")
);
""");

    public FullWorthDbContext CreateContext() => new(options);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
