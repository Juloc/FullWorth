using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830160000_InvestmentIntegrity")]
public partial class InvestmentIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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

DROP TRIGGER IF EXISTS "TR_InvestmentTrades_PreventOversell" ON "InvestmentTrades";
CREATE TRIGGER "TR_InvestmentTrades_PreventOversell"
BEFORE INSERT OR UPDATE OF "TradeType","TradeDate","Quantity","SecurityId","PortfolioId"
ON "InvestmentTrades"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_investment_sell();

CREATE INDEX IF NOT EXISTS "IX_InvestmentTrades_Portfolio_Security_Date"
  ON "InvestmentTrades"("PortfolioId","SecurityId","TradeDate","CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_SecurityPrices_Security_Date"
  ON "SecurityPrices"("SecurityId","PriceDate" DESC);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_InvestmentTrades_PreventOversell" ON "InvestmentTrades";
DROP FUNCTION IF EXISTS fullworth_validate_investment_sell();
DROP INDEX IF EXISTS "IX_InvestmentTrades_Portfolio_Security_Date";
DROP INDEX IF EXISTS "IX_SecurityPrices_Security_Date";
""");
    }
}