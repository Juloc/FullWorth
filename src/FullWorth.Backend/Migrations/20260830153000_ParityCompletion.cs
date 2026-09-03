using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830153000_ParityCompletion")]
public partial class ParityCompletion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "InvestmentPortfolios" ADD COLUMN IF NOT EXISTS "ProviderName" varchar(200) NULL;
ALTER TABLE "InvestmentPortfolios" ADD COLUMN IF NOT EXISTS "IsManual" boolean NOT NULL DEFAULT true;
ALTER TABLE "InvestmentPortfolios" ADD COLUMN IF NOT EXISTS "IncludeInNetWorth" boolean NOT NULL DEFAULT true;

ALTER TABLE "Securities" ADD COLUMN IF NOT EXISTS "ProviderKey" varchar(160) NULL;
ALTER TABLE "Securities" ADD COLUMN IF NOT EXISTS "IsActive" boolean NOT NULL DEFAULT true;

ALTER TABLE "InvestmentTrades" ADD COLUMN IF NOT EXISTS "SettlementDate" date NULL;
ALTER TABLE "InvestmentTrades" ADD COLUMN IF NOT EXISTS "GrossAmount" numeric(20,8) NULL;
ALTER TABLE "InvestmentTrades" ADD COLUMN IF NOT EXISTS "WithholdingTax" numeric(20,8) NOT NULL DEFAULT 0;
ALTER TABLE "InvestmentTrades" ADD COLUMN IF NOT EXISTS "Source" varchar(32) NOT NULL DEFAULT 'manual';

ALTER TABLE "WatchlistItems" ADD COLUMN IF NOT EXISTS "SortOrder" integer NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS "AccountGroupAppearances" (
  "GroupId" uuid PRIMARY KEY REFERENCES "AccountGroups"("Id") ON DELETE CASCADE,
  "Icon" varchar(64) NULL,
  "Color" varchar(9) NULL,
  "UpdatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS "ProductIdentities" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "CanonicalName" varchar(500) NOT NULL,
  "Brand" varchar(250) NULL,
  "Barcode" varchar(64) NULL,
  "DefaultCategoryId" uuid NULL REFERENCES "Categories"("Id") ON DELETE SET NULL,
  "UnitKind" varchar(32) NULL,
  "UnitSize" numeric(20,6) NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_ProductIdentities_Space_Name" ON "ProductIdentities"("FullWorthSpaceId","CanonicalName");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ProductIdentities_Space_Barcode" ON "ProductIdentities"("FullWorthSpaceId","Barcode") WHERE "Barcode" IS NOT NULL;

CREATE TABLE IF NOT EXISTS "ProductIdentityAliases" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "ProductIdentityId" uuid NOT NULL REFERENCES "ProductIdentities"("Id") ON DELETE CASCADE,
  "NormalizedText" varchar(500) NOT NULL,
  "Confidence" numeric(5,4) NULL,
  "Source" varchar(32) NOT NULL DEFAULT 'manual',
  "CreatedAt" timestamptz NOT NULL,
  UNIQUE ("FullWorthSpaceId","NormalizedText")
);
CREATE INDEX IF NOT EXISTS "IX_ProductIdentityAliases_Product" ON "ProductIdentityAliases"("ProductIdentityId");

CREATE TABLE IF NOT EXISTS "PurchaseItemProductLinks" (
  "PurchaseItemId" uuid PRIMARY KEY REFERENCES "PurchaseItems"("Id") ON DELETE CASCADE,
  "ProductIdentityId" uuid NOT NULL REFERENCES "ProductIdentities"("Id") ON DELETE RESTRICT,
  "Confidence" numeric(5,4) NULL,
  "Source" varchar(32) NOT NULL DEFAULT 'manual',
  "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_PurchaseItemProductLinks_Product" ON "PurchaseItemProductLinks"("ProductIdentityId");

CREATE TABLE IF NOT EXISTS "BenchmarkDefinitions" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "Name" varchar(160) NOT NULL,
  "SecurityId" uuid NULL REFERENCES "Securities"("Id") ON DELETE SET NULL,
  "ProviderSeriesKey" varchar(160) NULL,
  "IsBuiltIn" boolean NOT NULL DEFAULT false,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS "BankValidationRecords" (
  "Id" uuid PRIMARY KEY,
  "InstitutionKey" varchar(160) NOT NULL,
  "Provider" varchar(80) NOT NULL,
  "DisplayName" varchar(160) NOT NULL,
  "Country" varchar(2) NOT NULL,
  "IconAssetKey" varchar(160) NULL,
  "BalancesTested" boolean NOT NULL DEFAULT false,
  "TransactionsTested" boolean NOT NULL DEFAULT false,
  "PendingTested" boolean NOT NULL DEFAULT false,
  "MultiCurrencyTested" boolean NOT NULL DEFAULT false,
  "HistoryDepthDays" integer NULL,
  "LastValidatedAt" timestamptz NULL,
  "LastValidatedVersion" varchar(80) NULL,
  "KnownLimitations" varchar(2000) NULL,
  UNIQUE ("Provider","InstitutionKey")
);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "BankValidationRecords";
DROP TABLE IF EXISTS "BenchmarkDefinitions";
DROP TABLE IF EXISTS "PurchaseItemProductLinks";
DROP TABLE IF EXISTS "ProductIdentityAliases";
DROP TABLE IF EXISTS "ProductIdentities";
DROP TABLE IF EXISTS "AccountGroupAppearances";
ALTER TABLE "WatchlistItems" DROP COLUMN IF EXISTS "SortOrder";
ALTER TABLE "InvestmentTrades" DROP COLUMN IF EXISTS "Source";
ALTER TABLE "InvestmentTrades" DROP COLUMN IF EXISTS "WithholdingTax";
ALTER TABLE "InvestmentTrades" DROP COLUMN IF EXISTS "GrossAmount";
ALTER TABLE "InvestmentTrades" DROP COLUMN IF EXISTS "SettlementDate";
ALTER TABLE "Securities" DROP COLUMN IF EXISTS "IsActive";
ALTER TABLE "Securities" DROP COLUMN IF EXISTS "ProviderKey";
ALTER TABLE "InvestmentPortfolios" DROP COLUMN IF EXISTS "IncludeInNetWorth";
ALTER TABLE "InvestmentPortfolios" DROP COLUMN IF EXISTS "IsManual";
ALTER TABLE "InvestmentPortfolios" DROP COLUMN IF EXISTS "ProviderName";
""");
    }
}
