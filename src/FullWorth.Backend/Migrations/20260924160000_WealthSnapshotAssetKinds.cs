using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Der Tageswert haelt die Sachwerte jetzt AUFGETEILT fest, nicht mehr nur als eine Summe (#178).
///
/// Der Vermoegensverlauf kannte acht geforderte Reihen und konnte vier: Nettovermoegen, Konten,
/// Investments und die Schulden. Immobilien, Edelmetalle und sonstige Sachwerte steckten gemeinsam in
/// <c>ManualAssets</c>, und das Immobilien-Eigenkapital gab es gar nicht.
///
/// Gerechnet wird dafuer nichts Neues: die TAGESansicht teilt seit jeher genauso auf
/// (<c>WealthOverviewService</c> liefert die Immobilien als Teilmenge, mit derselben Umrechnung wie
/// die Gesamtsumme). Es fehlte nur, dieselbe Aufteilung auch festzuhalten.
///
/// Alle vier Spalten sind NULL-faehig, und das ist die eigentliche Aussage dieser Migration: ein Tag,
/// der vor heute festgehalten wurde, kennt seine Aufteilung nicht und bekommt sie auch nicht
/// nachtraeglich angerechnet. Die Kurve zeichnet dort eine Luecke, statt eine Zahl zu behaupten, die
/// niemand gemessen hat - dieselbe Regel, die <c>NetWorthHistoryStabilityTests</c> fuer die
/// vorhandenen Reihen haelt.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260924160000_WealthSnapshotAssetKinds")]
public sealed class WealthSnapshotAssetKinds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "RealEstateAssets" numeric(20,8) NULL;
        ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "PreciousMetalAssets" numeric(20,8) NULL;
        ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "PensionAssets" numeric(20,8) NULL;
        ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "OtherAssets" numeric(20,8) NULL;
        ALTER TABLE "NetWorthSnapshots" ADD COLUMN IF NOT EXISTS "RealEstateEquity" numeric(20,8) NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "RealEstateEquity";
        ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "OtherAssets";
        ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "PensionAssets";
        ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "PreciousMetalAssets";
        ALTER TABLE "NetWorthSnapshots" DROP COLUMN IF EXISTS "RealEstateAssets";
        """);
}
