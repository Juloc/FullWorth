using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260906104500_DepotImportProvenanceAndCancellation")]
public partial class DepotImportProvenanceAndCancellation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "InvestmentImportJobs" ADD COLUMN IF NOT EXISTS "PortfolioId" uuid NULL;
ALTER TABLE "InvestmentImportJobs" ADD COLUMN IF NOT EXISTS "PortfolioCreated" boolean NOT NULL DEFAULT false;
ALTER TABLE "InvestmentImportJobs" ADD COLUMN IF NOT EXISTS "RolledBackAt" timestamptz NULL;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'FK_InvestmentImportJobs_Portfolio'
  ) THEN
    ALTER TABLE "InvestmentImportJobs"
      ADD CONSTRAINT "FK_InvestmentImportJobs_Portfolio"
      FOREIGN KEY ("PortfolioId") REFERENCES "InvestmentPortfolios"("Id") ON DELETE SET NULL;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_InvestmentImportJobs_Portfolio"
  ON "InvestmentImportJobs"("PortfolioId","CreatedAt" DESC);

CREATE TABLE IF NOT EXISTS "InvestmentImportTradeLinks" (
  "ImportJobId" uuid NOT NULL REFERENCES "InvestmentImportJobs"("Id") ON DELETE CASCADE,
  "TradeId" uuid NOT NULL REFERENCES "InvestmentTrades"("Id") ON DELETE CASCADE,
  "CreatedAt" timestamptz NOT NULL,
  PRIMARY KEY ("ImportJobId","TradeId"),
  CONSTRAINT "UQ_InvestmentImportTradeLinks_Trade" UNIQUE ("TradeId")
);

CREATE TABLE IF NOT EXISTS "InvestmentImportSecurityLinks" (
  "ImportJobId" uuid NOT NULL REFERENCES "InvestmentImportJobs"("Id") ON DELETE CASCADE,
  "SecurityId" uuid NOT NULL REFERENCES "Securities"("Id") ON DELETE CASCADE,
  "CreatedAt" timestamptz NOT NULL,
  PRIMARY KEY ("ImportJobId","SecurityId")
);

CREATE INDEX IF NOT EXISTS "IX_InvestmentImportSecurityLinks_Security"
  ON "InvestmentImportSecurityLinks"("SecurityId");

CREATE OR REPLACE FUNCTION fullworth_validate_investment_sell() RETURNS trigger AS $$
DECLARE
  row_record record;
  owned numeric(24,10) := 0;
BEGIN
  IF NEW."TradeType" NOT IN ('sell','cancellation') THEN
    RETURN NEW;
  END IF;
  IF NEW."SecurityId" IS NULL OR NEW."Quantity" IS NULL OR NEW."Quantity" <= 0 THEN
    RAISE EXCEPTION 'Investment disposal requires a security and positive quantity';
  END IF;

  FOR row_record IN
    SELECT "TradeType", "Quantity"
    FROM "InvestmentTrades"
    WHERE "PortfolioId" = NEW."PortfolioId"
      AND "SecurityId" = NEW."SecurityId"
      AND "Id" <> NEW."Id"
      AND ("TradeDate" < NEW."TradeDate" OR ("TradeDate" = NEW."TradeDate" AND "CreatedAt" <= NEW."CreatedAt"))
    ORDER BY "TradeDate", "CreatedAt", "Id"
  LOOP
    IF row_record."TradeType" IN ('buy','security_transfer_in') THEN
      owned := owned + COALESCE(row_record."Quantity",0);
    ELSIF row_record."TradeType" IN ('sell','security_transfer_out','cancellation') THEN
      owned := owned - COALESCE(row_record."Quantity",0);
    ELSIF row_record."TradeType" = 'split' AND COALESCE(row_record."Quantity",0) > 0 THEN
      owned := owned * row_record."Quantity";
    END IF;
  END LOOP;

  IF NEW."Quantity" > owned + 0.0000000001 THEN
    RAISE EXCEPTION 'Cannot dispose % units; only % units are owned at the trade date', NEW."Quantity", owned;
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fullworth_assert_investment_position(p_portfolio uuid, p_security uuid)
RETURNS void AS $$
DECLARE
  row_record record;
  owned numeric(24,10) := 0;
BEGIN
  IF p_portfolio IS NULL OR p_security IS NULL THEN
    RETURN;
  END IF;

  FOR row_record IN
    SELECT "Id","TradeType","Quantity","TradeDate"
    FROM "InvestmentTrades"
    WHERE "PortfolioId" = p_portfolio AND "SecurityId" = p_security
    ORDER BY "TradeDate","CreatedAt","Id"
  LOOP
    IF row_record."TradeType" IN ('buy','security_transfer_in') THEN
      IF COALESCE(row_record."Quantity",0) <= 0 THEN
        RAISE EXCEPTION 'Investment acquisition requires positive quantity';
      END IF;
      owned := owned + row_record."Quantity";
    ELSIF row_record."TradeType" IN ('sell','security_transfer_out','cancellation') THEN
      IF COALESCE(row_record."Quantity",0) <= 0 THEN
        RAISE EXCEPTION 'Investment disposal requires positive quantity';
      END IF;
      owned := owned - row_record."Quantity";
      IF owned < -0.0000000001 THEN
        RAISE EXCEPTION 'Investment ledger is oversold on % (trade %): position %',
          row_record."TradeDate", row_record."Id", owned;
      END IF;
    ELSIF row_record."TradeType" = 'split' THEN
      IF COALESCE(row_record."Quantity",0) <= 0 THEN
        RAISE EXCEPTION 'Investment split ratio must be positive';
      END IF;
      owned := owned * row_record."Quantity";
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "InvestmentImportSecurityLinks";
DROP TABLE IF EXISTS "InvestmentImportTradeLinks";
DROP INDEX IF EXISTS "IX_InvestmentImportJobs_Portfolio";
ALTER TABLE "InvestmentImportJobs" DROP CONSTRAINT IF EXISTS "FK_InvestmentImportJobs_Portfolio";
ALTER TABLE "InvestmentImportJobs" DROP COLUMN IF EXISTS "RolledBackAt";
ALTER TABLE "InvestmentImportJobs" DROP COLUMN IF EXISTS "PortfolioCreated";
ALTER TABLE "InvestmentImportJobs" DROP COLUMN IF EXISTS "PortfolioId";

CREATE OR REPLACE FUNCTION fullworth_validate_investment_sell() RETURNS trigger AS $$
DECLARE
  row_record record;
  owned numeric(24,10) := 0;
BEGIN
  IF NEW."TradeType" <> 'sell' THEN
    RETURN NEW;
  END IF;
  IF NEW."SecurityId" IS NULL OR NEW."Quantity" IS NULL OR NEW."Quantity" <= 0 THEN
    RAISE EXCEPTION 'Sell requires a security and positive quantity';
  END IF;

  FOR row_record IN
    SELECT "TradeType", "Quantity"
    FROM "InvestmentTrades"
    WHERE "PortfolioId" = NEW."PortfolioId"
      AND "SecurityId" = NEW."SecurityId"
      AND "Id" <> NEW."Id"
      AND ("TradeDate" < NEW."TradeDate" OR ("TradeDate" = NEW."TradeDate" AND "CreatedAt" <= NEW."CreatedAt"))
    ORDER BY "TradeDate", "CreatedAt", "Id"
  LOOP
    IF row_record."TradeType" IN ('buy','security_transfer_in') THEN
      owned := owned + COALESCE(row_record."Quantity",0);
    ELSIF row_record."TradeType" IN ('sell','security_transfer_out') THEN
      owned := owned - COALESCE(row_record."Quantity",0);
    ELSIF row_record."TradeType" = 'split' AND COALESCE(row_record."Quantity",0) > 0 THEN
      owned := owned * row_record."Quantity";
    END IF;
  END LOOP;

  IF NEW."Quantity" > owned + 0.0000000001 THEN
    RAISE EXCEPTION 'Cannot sell % units; only % units are owned at the trade date', NEW."Quantity", owned;
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION fullworth_assert_investment_position(p_portfolio uuid, p_security uuid)
RETURNS void AS $$
DECLARE
  row_record record;
  owned numeric(24,10) := 0;
BEGIN
  IF p_portfolio IS NULL OR p_security IS NULL THEN
    RETURN;
  END IF;

  FOR row_record IN
    SELECT "Id","TradeType","Quantity","TradeDate"
    FROM "InvestmentTrades"
    WHERE "PortfolioId" = p_portfolio AND "SecurityId" = p_security
    ORDER BY "TradeDate","CreatedAt","Id"
  LOOP
    IF row_record."TradeType" IN ('buy','security_transfer_in') THEN
      IF COALESCE(row_record."Quantity",0) <= 0 THEN
        RAISE EXCEPTION 'Investment acquisition requires positive quantity';
      END IF;
      owned := owned + row_record."Quantity";
    ELSIF row_record."TradeType" IN ('sell','security_transfer_out') THEN
      IF COALESCE(row_record."Quantity",0) <= 0 THEN
        RAISE EXCEPTION 'Investment disposal requires positive quantity';
      END IF;
      owned := owned - row_record."Quantity";
      IF owned < -0.0000000001 THEN
        RAISE EXCEPTION 'Investment ledger is oversold on % (trade %): position %',
          row_record."TradeDate", row_record."Id", owned;
      END IF;
    ELSIF row_record."TradeType" = 'split' THEN
      IF COALESCE(row_record."Quantity",0) <= 0 THEN
        RAISE EXCEPTION 'Investment split ratio must be positive';
      END IF;
      owned := owned * row_record."Quantity";
    END IF;
  END LOOP;
END;
$$ LANGUAGE plpgsql;
""");
    }
}
