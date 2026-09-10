using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// A balance said what it was (the bank's balance_type) but never where it came from. A value the owner
/// typed in and a value a bank reported were indistinguishable on screen, which matters most exactly
/// where there is no bank: an imported or unconnected account whose only balance is one somebody
/// entered. Source records the origin, Note the owner's own remark, and the existing ReferenceDate
/// finally gets used as the as-of date instead of always being stamped with today.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260910210000_BalanceProvenance")]
public sealed class BalanceProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "BalanceSnapshots"
  ADD COLUMN IF NOT EXISTS "Source" character varying(32) NULL;
ALTER TABLE "BalanceSnapshots"
  ADD COLUMN IF NOT EXISTS "Note" character varying(200) NULL;

-- Existing rows keep their meaning: everything the two manual write paths produced is manual, the rest
-- arrived from a provider sync. Nothing is deleted and no amount is touched.
UPDATE "BalanceSnapshots"
SET "Source" = CASE WHEN "BalanceType" IN ('manual', 'manualCurrent') THEN 'manual' ELSE 'provider' END
WHERE "Source" IS NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "BalanceSnapshots"
  DROP COLUMN IF EXISTS "Note";
ALTER TABLE "BalanceSnapshots"
  DROP COLUMN IF EXISTS "Source";
""");
    }
}
