using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Effektivkosten (Reduction in Yield) gets its own cost kind.
///
/// A German Standmitteilung prints it as the one figure that says what all the other costs do
/// <i>together</i>, expressed as the yield they take per year — and it is the figure a person actually
/// compares two contracts by. The statement parser had nowhere to put it: the closest existing kind was
/// <c>other</c>, which left it unlabelled on screen, and folding it into a named kind such as
/// <c>administration_on_capital</c> would have been worse, because it would double-count every component
/// beneath it in any total.
///
/// So it is its own value, and <c>BavCostKinds.IsAggregate</c> marks it as an aggregate: a sum over costs
/// has to exclude it and show it beside the total rather than inside it.
///
/// Purely additive to the check constraint. No row is touched, no existing kind changes meaning, and a
/// cost already stored as <c>other</c> stays <c>other</c> — re-reading a document is what reclassifies
/// it, not a migration guessing at what an old row meant.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260911020000_PensionEffectiveCost")]
public sealed class PensionEffectiveCost : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "BavCosts" DROP CONSTRAINT IF EXISTS "CK_BavCosts_Kind";
ALTER TABLE "BavCosts" ADD CONSTRAINT "CK_BavCosts_Kind" CHECK ("Kind" IN (
  'acquisition','administration_on_contribution','administration_on_capital','administration_fixed',
  'fund','guarantee','risk_premium','payout','effective_cost','other'));
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverting narrows the constraint, so any row that used the new kind has to be reclassified
        // first — as `other`, which is exactly where such a cost lived before this migration existed.
        migrationBuilder.Sql("""
UPDATE "BavCosts" SET "Kind" = 'other' WHERE "Kind" = 'effective_cost';
ALTER TABLE "BavCosts" DROP CONSTRAINT IF EXISTS "CK_BavCosts_Kind";
ALTER TABLE "BavCosts" ADD CONSTRAINT "CK_BavCosts_Kind" CHECK ("Kind" IN (
  'acquisition','administration_on_contribution','administration_on_capital','administration_fixed',
  'fund','guarantee','risk_premium','payout','other'));
""");
    }
}
