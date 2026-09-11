using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// A contract can say whether it counts as a fixed cost.
///
/// Until now every consumer that turned contracts into a cost figure — the cashflow's "what is
/// available" figure and the reconciliation report — treated <b>every active contract</b> as a fixed
/// cost. There was no way to say otherwise, and the only lever available, deactivating the contract,
/// also removes it from the list where it belongs.
///
/// That is wrong for more than one real case: a recurring contract that is really a savings plan, one
/// whose payment is already counted somewhere else, or one the owner simply does not want in the
/// forecast. Turning the flag off changes no history and no transaction — it only says: do not subtract
/// this from what is available.
///
/// <c>DEFAULT TRUE</c> and backfilled to TRUE, because that is exactly what every consumer assumed
/// before the column existed. Nobody's cashflow figure moves the moment this migration runs; it moves
/// only when the owner unticks something.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260911140000_ContractCountsAsFixedCost")]
public sealed class ContractCountsAsFixedCost : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Contracts"
  ADD COLUMN IF NOT EXISTS "CountsAsFixedCost" boolean NOT NULL DEFAULT TRUE;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Contracts"
  DROP COLUMN IF EXISTS "CountsAsFixedCost";
""");
    }
}
