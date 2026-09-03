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
""");

    public FullWorthDbContext CreateContext() => new(options);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
