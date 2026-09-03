using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830162000_MarketDataFreshness")]
public partial class MarketDataFreshness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "SecurityPrices" ADD COLUMN IF NOT EXISTS "FetchedAt" timestamptz NULL;
UPDATE "SecurityPrices" SET "FetchedAt"="CreatedAt" WHERE "FetchedAt" IS NULL;
CREATE INDEX IF NOT EXISTS "IX_SecurityPrices_EffectiveLookup"
  ON "SecurityPrices"("SecurityId","PriceDate" DESC,"FetchedAt" DESC);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_SecurityPrices_EffectiveLookup";
ALTER TABLE "SecurityPrices" DROP COLUMN IF EXISTS "FetchedAt";
""");
    }
}
