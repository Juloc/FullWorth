using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Der Einstandskurs, den die Bank zu einem eingebuchten Bestand nennt.
///
/// Ein Gewinn braucht zwei Zahlen: den heutigen Wert und den Einstand. Aus HKWPD kam bisher nur die
/// erste, und ein <c>security_transfer_in</c> hiess deshalb "Stuecke ohne Einstand" - die Position
/// meldete <c>CostBasisIncomplete</c> und weder Einstand noch Ergebnis. Die ING nennt ihn aber, im
/// Fliesstext jedes Blocks:
///
/// <code>
/// :70E::HOLD//1STK
/// 257,128493+EUR
/// </code>
///
/// Eine EIGENE Spalte, und das ist der Punkt. Naheliegend waere gewesen, <c>Price</c> oder
/// <c>GrossAmount</c> umzudeuten - beide sind an diesen Zeilen belegt, mit dem KURSWERT. Jede
/// Umdeutung haette alle bereits geschriebenen Zeilen rueckwirkend zu Einstaenden erklaert und aus
/// einem Kurswert einen Gewinn von null gemacht. Eine neue Spalte ist fuer jede Altzeile NULL, und
/// NULL heisst hier weiterhin: unbekannt, nicht null.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260917140000_InvestmentTradeCostPrice")]
public sealed class InvestmentTradeCostPrice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "InvestmentTrades"
  ADD COLUMN IF NOT EXISTS "CostPrice" numeric(20,8) NULL;
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "InvestmentTrades"
  DROP COLUMN IF EXISTS "CostPrice";
""");
}
