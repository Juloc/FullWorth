using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830164000_InvestmentImportStaging")]
public partial class InvestmentImportStaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "InvestmentImportJobs" (
  "Id" uuid PRIMARY KEY,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
  "FileName" varchar(260) NOT NULL,
  "FileSha256" varchar(64) NOT NULL,
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
CREATE INDEX IF NOT EXISTS "IX_InvestmentImportJobs_Space_User"
  ON "InvestmentImportJobs"("FullWorthSpaceId","UserId","CreatedAt" DESC);

CREATE TABLE IF NOT EXISTS "InvestmentImportCandidates" (
  "Id" uuid PRIMARY KEY,
  "ImportJobId" uuid NOT NULL REFERENCES "InvestmentImportJobs"("Id") ON DELETE CASCADE,
  "RowNumber" integer NOT NULL,
  "TradeDate" date NULL,
  "SettlementDate" date NULL,
  "TradeType" varchar(32) NULL,
  "SecurityName" varchar(240) NULL,
  "Isin" varchar(12) NULL,
  "Wkn" varchar(12) NULL,
  "Ticker" varchar(32) NULL,
  "Quantity" numeric(24,10) NULL,
  "Price" numeric(24,10) NULL,
  "GrossAmount" numeric(20,8) NULL,
  "Amount" numeric(20,8) NOT NULL DEFAULT 0,
  "Currency" varchar(3) NOT NULL DEFAULT 'EUR',
  "Fees" numeric(20,8) NOT NULL DEFAULT 0,
  "Taxes" numeric(20,8) NOT NULL DEFAULT 0,
  "WithholdingTax" numeric(20,8) NOT NULL DEFAULT 0,
  "ExternalKey" varchar(300) NULL,
  "RowFingerprint" varchar(64) NOT NULL,
  "ValidationStatus" varchar(24) NOT NULL DEFAULT 'ready',
  "DuplicateStatus" varchar(24) NOT NULL DEFAULT 'new',
  "ValidationError" varchar(1000) NULL,
  "CreatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_InvestmentImportCandidates_Job"
  ON "InvestmentImportCandidates"("ImportJobId","RowNumber");
CREATE INDEX IF NOT EXISTS "IX_InvestmentImportCandidates_Fingerprint"
  ON "InvestmentImportCandidates"("RowFingerprint");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "InvestmentImportCandidates";
DROP TABLE IF EXISTS "InvestmentImportJobs";
""");
    }
}
