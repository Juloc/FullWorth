using FullWorth.Backend.Modules.Portfolio;
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
        await CreateRawTransactionTablesAsync(db);
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

    // Aus demselben Grund wie oben, nur fuer die andere Haelfte: diese acht Tabellen zeigen per
    // Fremdschluessel auf "Transactions", stehen aber nicht im EF-Modell - sie entstehen in rohen
    // SQL-Migrationen. EnsureCreated baut das Schema aus dem Modell und legt sie darum nie an.
    //
    // Das faellt auf, seit das Zusammenfuehren zweier Buchungen JEDE Beziehung umhaengt statt vier
    // (TransactionMergeService): auf PostgreSQL sind alle da, auf diesem SQLite waren sie es nicht,
    // und eine Zusammenfuehrung waere hier mit "no such table" gescheitert - an einer Luecke der
    // Testkulisse, nicht an einem Fehler im Produkt. Die Spalten sind die des echten Schemas.
    //
    // "ImportTransactionLinks" kam spaeter dazu (#175): eine Finanzguru-Kontoverknuepfung loescht seither
    // die Herkunftsverknuepfung einer verschobenen (nicht zusammengefuehrten) Buchung selbst mit -
    // vorher war das die einzige der acht Tabellen, die dieser Codepfad nie anfasste.
    //
    // "ImportTransactionEnrichments" kam mit #131 dazu und wandert beim Zusammenfuehren mit, also
    // muss sie hier stehen - sonst scheitert jede Zusammenfuehrung in diesen Tests an "no such
    // table", was nach einem Produktfehler aussieht und keiner ist.
    private static Task CreateRawTransactionTablesAsync(FullWorthDbContext db) =>
        db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "AssetCashflowEntries" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL,
  "AssetId" uuid NOT NULL,
  "TransactionId" uuid NULL,
  "Date" text NOT NULL,
  "Type" varchar(32) NOT NULL,
  "Amount" numeric NOT NULL,
  "Direction" varchar(16) NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "IsPlanned" integer NOT NULL DEFAULT 0,
  "Notes" varchar(500) NULL,
  "CreatedAt" text NOT NULL,
  "UpdatedAt" text NOT NULL
);
CREATE TABLE IF NOT EXISTS "ContractTransactionLinks" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL,
  "ContractId" uuid NOT NULL,
  "TransactionId" uuid NOT NULL,
  "Amount" numeric NOT NULL,
  "LinkSource" varchar(32) NOT NULL,
  "Confidence" numeric NULL,
  "CreatedAt" text NOT NULL,
  UNIQUE ("ContractId","TransactionId")
);
CREATE TABLE IF NOT EXISTS "PurchaseRefunds" (
  "Id" uuid PRIMARY KEY,
  "PurchaseId" uuid NOT NULL,
  "ExternalRefundId" varchar(120) NOT NULL,
  "RefundDate" text NULL,
  "Amount" numeric NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Status" varchar(32) NOT NULL,
  "Description" varchar(500) NULL,
  "TransactionId" uuid NULL,
  "MatchConfidence" numeric NULL,
  "CreatedAt" text NOT NULL,
  "UpdatedAt" text NOT NULL,
  UNIQUE ("TransactionId")
);
CREATE TABLE IF NOT EXISTS "ReceivablePayments" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL,
  "AssetId" uuid NOT NULL,
  "TransactionId" uuid NULL,
  "Date" text NOT NULL,
  "PrincipalAmount" numeric NOT NULL,
  "InterestAmount" numeric NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Notes" varchar(500) NULL,
  "CreatedByUserId" uuid NULL,
  "CreatedAt" text NOT NULL
);
CREATE TABLE IF NOT EXISTS "TransactionReviewStates" (
  "TransactionId" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL,
  "IsReviewed" integer NOT NULL DEFAULT 0,
  "UpdatedAt" text NOT NULL
);
CREATE TABLE IF NOT EXISTS "TransactionTags" (
  "TransactionId" uuid NOT NULL,
  "TagId" uuid NOT NULL,
  "CreatedAt" text NOT NULL,
  PRIMARY KEY ("TransactionId","TagId")
);
CREATE TABLE IF NOT EXISTS "RefundSuggestionDismissals" (
  "FullWorthSpaceId" uuid NOT NULL,
  "RefundTransactionId" uuid NOT NULL,
  "OriginalTransactionId" uuid NOT NULL,
  "DismissedAt" text NOT NULL,
  PRIMARY KEY ("RefundTransactionId","OriginalTransactionId")
);
CREATE TABLE IF NOT EXISTS "SpendingReviews" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL,
  "UserId" uuid NOT NULL,
  "TransactionId" uuid NOT NULL,
  "PurchaseId" uuid NULL,
  "Sentiment" varchar(32) NOT NULL,
  "ReasonsJson" text NOT NULL,
  "Note" varchar(500) NULL,
  "CreatedAt" text NOT NULL,
  "UpdatedAt" text NOT NULL,
  UNIQUE ("FullWorthSpaceId","UserId","TransactionId")
);
CREATE TABLE IF NOT EXISTS "ImportTransactionLinks" (
  "ImportJobId" uuid NOT NULL,
  "TransactionId" uuid NOT NULL,
  "CreatedAt" text NOT NULL,
  PRIMARY KEY ("ImportJobId","TransactionId"),
  UNIQUE ("TransactionId")
);
CREATE TABLE IF NOT EXISTS "ImportTransactionEnrichments" (
  "ImportJobId" uuid NOT NULL,
  "TransactionId" uuid NOT NULL,
  "SetCategoryId" uuid NULL,
  "SetTransfer" integer NOT NULL DEFAULT 0,
  "CreatedAt" text NOT NULL,
  PRIMARY KEY ("ImportJobId","TransactionId")
);
""");

    public FullWorthDbContext CreateContext() => new(options);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
