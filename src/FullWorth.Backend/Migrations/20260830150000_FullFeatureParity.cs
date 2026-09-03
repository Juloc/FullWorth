using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830150000_FullFeatureParity")]
public partial class FullFeatureParity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "IncomeSchedules" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "Name" varchar(160) NOT NULL,
  "AccountId" uuid NULL REFERENCES "Accounts"("Id") ON DELETE SET NULL,
  "NormalizedCounterparty" varchar(512) NULL,
  "ExpectedAmount" numeric(20,8) NULL,
  "Currency" varchar(3) NOT NULL,
  "Cycle" varchar(24) NOT NULL,
  "Interval" integer NOT NULL DEFAULT 1,
  "AnchorDate" date NULL,
  "NextExpectedDate" date NULL,
  "ValueMode" varchar(16) NOT NULL DEFAULT 'manual',
  "AutoDetected" boolean NOT NULL DEFAULT false,
  "IsActive" boolean NOT NULL DEFAULT true,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_IncomeSchedules_FullWorthSpaceId_IsActive" ON "IncomeSchedules"("FullWorthSpaceId","IsActive");

CREATE TABLE IF NOT EXISTS "CashflowPlanSettings" (
  "FullWorthSpaceId" uuid PRIMARY KEY REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "HorizonMode" varchar(24) NOT NULL DEFAULT 'next_income',
  "SafetyReserveAmount" numeric(20,8) NOT NULL DEFAULT 0,
  "SafetyReserveCurrency" varchar(3) NOT NULL DEFAULT 'EUR',
  "IncludePendingIncome" boolean NOT NULL DEFAULT false,
  "IncludePendingExpenses" boolean NOT NULL DEFAULT false,
  "VariableForecastMode" varchar(24) NOT NULL DEFAULT 'pace_blend',
  "UpdatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS "BudgetCategories" (
  "BudgetId" uuid NOT NULL REFERENCES "Budgets"("Id") ON DELETE CASCADE,
  "CategoryId" uuid NOT NULL REFERENCES "Categories"("Id") ON DELETE RESTRICT,
  "IncludeDescendants" boolean NOT NULL DEFAULT true,
  PRIMARY KEY ("BudgetId","CategoryId")
);
CREATE TABLE IF NOT EXISTS "BudgetAccounts" (
  "BudgetId" uuid NOT NULL REFERENCES "Budgets"("Id") ON DELETE CASCADE,
  "AccountId" uuid NOT NULL REFERENCES "Accounts"("Id") ON DELETE RESTRICT,
  PRIMARY KEY ("BudgetId","AccountId")
);
CREATE TABLE IF NOT EXISTS "BudgetTags" (
  "BudgetId" uuid NOT NULL REFERENCES "Budgets"("Id") ON DELETE CASCADE,
  "TagId" uuid NOT NULL REFERENCES "FinanceTags"("Id") ON DELETE CASCADE,
  PRIMARY KEY ("BudgetId","TagId")
);
CREATE TABLE IF NOT EXISTS "BudgetMerchants" (
  "BudgetId" uuid NOT NULL REFERENCES "Budgets"("Id") ON DELETE CASCADE,
  "NormalizedMerchant" varchar(512) NOT NULL,
  PRIMARY KEY ("BudgetId","NormalizedMerchant")
);
CREATE TABLE IF NOT EXISTS "BudgetAdvancedSettings" (
  "BudgetId" uuid PRIMARY KEY REFERENCES "Budgets"("Id") ON DELETE CASCADE,
  "IncomeScheduleId" uuid NULL REFERENCES "IncomeSchedules"("Id") ON DELETE SET NULL,
  "AlertNearPercent" numeric(8,2) NOT NULL DEFAULT 80,
  "AlertCriticalPercent" numeric(8,2) NOT NULL DEFAULT 100,
  "ScopeVersion" integer NOT NULL DEFAULT 1,
  "GroupId" uuid NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS "BudgetGroups" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "Name" varchar(120) NOT NULL,
  "SortOrder" integer NOT NULL DEFAULT 0,
  "IsArchived" boolean NOT NULL DEFAULT false,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_BudgetGroups_FullWorthSpaceId" ON "BudgetGroups"("FullWorthSpaceId","IsArchived","SortOrder");

CREATE TABLE IF NOT EXISTS "ContractBundles" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "Name" varchar(160) NOT NULL,
  "ProviderName" varchar(200) NULL,
  "AccountId" uuid NULL REFERENCES "Accounts"("Id") ON DELETE SET NULL,
  "Currency" varchar(3) NOT NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS "ContractBundleMembers" (
  "BundleId" uuid NOT NULL REFERENCES "ContractBundles"("Id") ON DELETE CASCADE,
  "ContractId" uuid NOT NULL REFERENCES "Contracts"("Id") ON DELETE CASCADE,
  PRIMARY KEY ("BundleId","ContractId")
);
CREATE TABLE IF NOT EXISTS "ContractTransactionLinks" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "ContractId" uuid NOT NULL REFERENCES "Contracts"("Id") ON DELETE CASCADE,
  "TransactionId" uuid NOT NULL REFERENCES "Transactions"("Id") ON DELETE CASCADE,
  "Amount" numeric(20,8) NOT NULL,
  "LinkSource" varchar(16) NOT NULL,
  "Confidence" numeric(5,4) NULL,
  "CreatedAt" timestamptz NOT NULL,
  UNIQUE ("ContractId","TransactionId")
);
CREATE INDEX IF NOT EXISTS "IX_ContractTransactionLinks_TransactionId" ON "ContractTransactionLinks"("TransactionId");
CREATE TABLE IF NOT EXISTS "ContractCancellationDetails" (
  "ContractId" uuid PRIMARY KEY REFERENCES "Contracts"("Id") ON DELETE CASCADE,
  "MinimumTermEnd" date NULL,
  "NoticePeriodValue" integer NULL,
  "NoticePeriodUnit" varchar(16) NULL,
  "RenewalPeriodValue" integer NULL,
  "RenewalPeriodUnit" varchar(16) NULL,
  "AutoRenews" boolean NOT NULL DEFAULT false,
  "CancellationDeadline" date NULL,
  "CancellationStatus" varchar(16) NOT NULL DEFAULT 'none',
  "CancellationSentAt" timestamptz NULL,
  "CancellationConfirmedAt" timestamptz NULL,
  "CustomerNumber" varchar(160) NULL,
  "ProviderContact" varchar(500) NULL,
  "UpdatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS "RefundSuggestionDismissals" (
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "RefundTransactionId" uuid NOT NULL REFERENCES "Transactions"("Id") ON DELETE CASCADE,
  "OriginalTransactionId" uuid NOT NULL REFERENCES "Transactions"("Id") ON DELETE CASCADE,
  "DismissedAt" timestamptz NOT NULL,
  PRIMARY KEY ("RefundTransactionId","OriginalTransactionId")
);

CREATE TABLE IF NOT EXISTS "SavedAnalyses" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "OwnerUserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
  "Name" varchar(160) NOT NULL,
  "SchemaVersion" integer NOT NULL DEFAULT 1,
  "ConfigJson" jsonb NOT NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_SavedAnalyses_Owner" ON "SavedAnalyses"("FullWorthSpaceId","OwnerUserId");

CREATE TABLE IF NOT EXISTS "ImportJobs" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
  "FileName" varchar(260) NOT NULL,
  "FileSha256" varchar(64) NOT NULL,
  "AdapterKey" varchar(64) NOT NULL,
  "Status" varchar(32) NOT NULL,
  "SourceRowCount" integer NOT NULL DEFAULT 0,
  "ReadyCount" integer NOT NULL DEFAULT 0,
  "DuplicateCount" integer NOT NULL DEFAULT 0,
  "ImportedCount" integer NOT NULL DEFAULT 0,
  "ErrorCount" integer NOT NULL DEFAULT 0,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL,
  "CompletedAt" timestamptz NULL
);
CREATE TABLE IF NOT EXISTS "ImportCandidates" (
  "Id" uuid PRIMARY KEY,
  "ImportJobId" uuid NOT NULL REFERENCES "ImportJobs"("Id") ON DELETE CASCADE,
  "SourceAccount" varchar(300) NULL,
  "BookingDate" date NULL,
  "ValueDate" date NULL,
  "Amount" numeric(20,8) NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Counterparty" varchar(500) NULL,
  "Description" varchar(2000) NULL,
  "CategoryText" varchar(500) NULL,
  "ExternalKey" varchar(500) NULL,
  "RowFingerprint" varchar(64) NOT NULL,
  "DuplicateStatus" varchar(24) NOT NULL DEFAULT 'new',
  "ValidationStatus" varchar(24) NOT NULL DEFAULT 'ready',
  "ValidationError" varchar(1000) NULL,
  "RawSourceEncrypted" text NULL
);
CREATE INDEX IF NOT EXISTS "IX_ImportCandidates_Job" ON "ImportCandidates"("ImportJobId");

CREATE TABLE IF NOT EXISTS "InvestmentPortfolios" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "Name" varchar(160) NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "AccountId" uuid NULL REFERENCES "Accounts"("Id") ON DELETE SET NULL,
  "BenchmarkSecurityId" uuid NULL,
  "IsArchived" boolean NOT NULL DEFAULT false,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS "Securities" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "Name" varchar(240) NOT NULL,
  "Isin" varchar(12) NULL,
  "Wkn" varchar(12) NULL,
  "Ticker" varchar(32) NULL,
  "AssetType" varchar(32) NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Exchange" varchar(80) NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Securities_Space_Isin" ON "Securities"("FullWorthSpaceId","Isin") WHERE "Isin" IS NOT NULL;
ALTER TABLE "InvestmentPortfolios" ADD CONSTRAINT "FK_InvestmentPortfolios_BenchmarkSecurity" FOREIGN KEY ("BenchmarkSecurityId") REFERENCES "Securities"("Id") ON DELETE SET NULL;
CREATE TABLE IF NOT EXISTS "InvestmentTrades" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "PortfolioId" uuid NOT NULL REFERENCES "InvestmentPortfolios"("Id") ON DELETE CASCADE,
  "SecurityId" uuid NULL REFERENCES "Securities"("Id") ON DELETE RESTRICT,
  "TradeType" varchar(24) NOT NULL,
  "TradeDate" date NOT NULL,
  "Quantity" numeric(24,10) NULL,
  "Price" numeric(24,10) NULL,
  "Amount" numeric(20,8) NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Fees" numeric(20,8) NOT NULL DEFAULT 0,
  "Taxes" numeric(20,8) NOT NULL DEFAULT 0,
  "ExternalKey" varchar(300) NULL,
  "Notes" varchar(1000) NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_InvestmentTrades_Portfolio_Date" ON "InvestmentTrades"("PortfolioId","TradeDate");
CREATE TABLE IF NOT EXISTS "SecurityPrices" (
  "SecurityId" uuid NOT NULL REFERENCES "Securities"("Id") ON DELETE CASCADE,
  "PriceDate" date NOT NULL,
  "Price" numeric(24,10) NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Source" varchar(64) NOT NULL,
  "CreatedAt" timestamptz NOT NULL,
  PRIMARY KEY ("SecurityId","PriceDate","Source")
);
CREATE TABLE IF NOT EXISTS "Watchlists" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "OwnerUserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
  "Name" varchar(160) NOT NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS "WatchlistItems" (
  "WatchlistId" uuid NOT NULL REFERENCES "Watchlists"("Id") ON DELETE CASCADE,
  "SecurityId" uuid NOT NULL REFERENCES "Securities"("Id") ON DELETE CASCADE,
  "TargetPrice" numeric(24,10) NULL,
  "Notes" varchar(500) NULL,
  PRIMARY KEY ("WatchlistId","SecurityId")
);

CREATE TABLE IF NOT EXISTS "AccountAppearances" (
  "AccountId" uuid PRIMARY KEY REFERENCES "Accounts"("Id") ON DELETE CASCADE,
  "Icon" varchar(64) NULL,
  "IconColor" varchar(9) NULL,
  "BackgroundColor" varchar(9) NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS "AccountTransactionSeenStates" (
  "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
  "AccountId" uuid NOT NULL REFERENCES "Accounts"("Id") ON DELETE CASCADE,
  "LastSeenAt" timestamptz NOT NULL,
  PRIMARY KEY ("UserId","AccountId")
);

CREATE TABLE IF NOT EXISTS "FinanceCapabilityGrants" (
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
  "Capability" varchar(80) NOT NULL,
  "IsAllowed" boolean NOT NULL,
  "UpdatedAt" timestamptz NOT NULL,
  PRIMARY KEY ("FullWorthSpaceId","UserId","Capability")
);
""");
        // NOTE: "ProductAliases" is created and owned by the earlier 20260830133000_PurchasesArticlesSystem
        // migration (canonical shape: Id, ProductId, MerchantId, Alias, NormalizedAlias, AliasType). This
        // migration must not re-declare or drop it — doing so caused rollbacks past this point to drop the
        // live canonical table and then fail PurchasesArticlesSystem.Down() with 42P01.

        // Backfill legacy single-category budgets into the normalized scope table without changing the old column.
        migrationBuilder.Sql("""
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
SELECT "Id","CategoryId",true FROM "Budgets" WHERE "CategoryId" IS NOT NULL
ON CONFLICT ("BudgetId","CategoryId") DO NOTHING;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "FinanceCapabilityGrants";
DROP TABLE IF EXISTS "AccountTransactionSeenStates";
DROP TABLE IF EXISTS "AccountAppearances";
DROP TABLE IF EXISTS "WatchlistItems";
DROP TABLE IF EXISTS "Watchlists";
DROP TABLE IF EXISTS "SecurityPrices";
DROP TABLE IF EXISTS "InvestmentTrades";
ALTER TABLE IF EXISTS "InvestmentPortfolios" DROP CONSTRAINT IF EXISTS "FK_InvestmentPortfolios_BenchmarkSecurity";
DROP TABLE IF EXISTS "InvestmentPortfolios";
DROP TABLE IF EXISTS "Securities";
DROP TABLE IF EXISTS "ImportCandidates";
DROP TABLE IF EXISTS "ImportJobs";
DROP TABLE IF EXISTS "SavedAnalyses";
DROP TABLE IF EXISTS "RefundSuggestionDismissals";
DROP TABLE IF EXISTS "ContractCancellationDetails";
DROP TABLE IF EXISTS "ContractTransactionLinks";
DROP TABLE IF EXISTS "ContractBundleMembers";
DROP TABLE IF EXISTS "ContractBundles";
DROP TABLE IF EXISTS "BudgetAdvancedSettings";
DROP TABLE IF EXISTS "BudgetMerchants";
DROP TABLE IF EXISTS "BudgetTags";
DROP TABLE IF EXISTS "BudgetAccounts";
DROP TABLE IF EXISTS "BudgetCategories";
DROP TABLE IF EXISTS "BudgetGroups";
DROP TABLE IF EXISTS "CashflowPlanSettings";
DROP TABLE IF EXISTS "IncomeSchedules";
""");
    }
}
