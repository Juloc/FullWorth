using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830161000_InvestmentLedgerIntegrity")]
public partial class InvestmentLedgerIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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

CREATE OR REPLACE FUNCTION fullworth_validate_investment_ledger_change()
RETURNS trigger AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    PERFORM fullworth_assert_investment_position(OLD."PortfolioId", OLD."SecurityId");
    RETURN OLD;
  END IF;

  PERFORM fullworth_assert_investment_position(NEW."PortfolioId", NEW."SecurityId");

  IF TG_OP = 'UPDATE' AND
     (OLD."PortfolioId" IS DISTINCT FROM NEW."PortfolioId" OR OLD."SecurityId" IS DISTINCT FROM NEW."SecurityId") THEN
    PERFORM fullworth_assert_investment_position(OLD."PortfolioId", OLD."SecurityId");
  END IF;

  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS "TR_InvestmentTrades_ValidateLedger" ON "InvestmentTrades";
CREATE CONSTRAINT TRIGGER "TR_InvestmentTrades_ValidateLedger"
AFTER INSERT OR UPDATE OR DELETE ON "InvestmentTrades"
DEFERRABLE INITIALLY IMMEDIATE
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_investment_ledger_change();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_InvestmentTrades_ValidateLedger" ON "InvestmentTrades";
DROP FUNCTION IF EXISTS fullworth_validate_investment_ledger_change();
DROP FUNCTION IF EXISTS fullworth_assert_investment_position(uuid, uuid);
""");
    }
}
