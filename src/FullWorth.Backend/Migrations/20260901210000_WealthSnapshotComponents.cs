using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260901210000_WealthSnapshotComponents")]
public sealed class WealthSnapshotComponents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "ManualAssets" numeric(20,8) NULL;
ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "Investments" numeric(20,8) NULL;
ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "Loans" numeric(20,8) NULL;
ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "OtherLiabilities" numeric(20,8) NULL;
ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "ComponentCurrency" varchar(3) NULL;
ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "IsComplete" boolean NULL;
ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "MissingCurrenciesJson" jsonb NULL;

ALTER TABLE "NetWorthSnapshots" DROP CONSTRAINT IF EXISTS "CK_NetWorthSnapshots_ComponentCurrency";
ALTER TABLE "NetWorthSnapshots" ADD CONSTRAINT "CK_NetWorthSnapshots_ComponentCurrency"
  CHECK ("ComponentCurrency" IS NULL OR "ComponentCurrency" ~ '^[A-Z]{3}$');

CREATE INDEX IF NOT EXISTS "IX_NetWorthSnapshots_WealthHistory"
  ON "NetWorthSnapshots" ("FullWorthSpaceId", "UserId", "Date");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_NetWorthSnapshots_WealthComponents"
  ON "NetWorthSnapshots" ("FullWorthSpaceId", "UserId", "Date")
  WHERE "ManualAssets" IS NOT NULL OR "Investments" IS NOT NULL OR "Loans" IS NOT NULL OR "OtherLiabilities" IS NOT NULL;

-- Deliberately do not decompose old rows. Legacy snapshots did not record which part of Assets was
-- investments versus manual assets, and Loans were not included at all. Fabricating a decomposition
-- would create false history. V2 history therefore treats NULL component columns as legacy/incomplete.
-- Component values intentionally have no positivity CHECK: the investment ledger can represent
-- negative cash/margin positions, and snapshot persistence must preserve canonical source semantics.
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "UX_NetWorthSnapshots_WealthComponents";
DROP INDEX IF EXISTS "IX_NetWorthSnapshots_WealthHistory";
ALTER TABLE "NetWorthSnapshots" DROP CONSTRAINT IF EXISTS "CK_NetWorthSnapshots_ComponentCurrency";
ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "MissingCurrenciesJson";
ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "IsComplete";
ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "ComponentCurrency";
ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "OtherLiabilities";
ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "Loans";
ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "Investments";
ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "ManualAssets";
""");
    }
}
