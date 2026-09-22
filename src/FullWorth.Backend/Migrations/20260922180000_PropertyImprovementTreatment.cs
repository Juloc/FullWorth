using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Instandhaltung oder wertsteigernd, je Massnahme (#174).
///
/// NULL heisst "nicht entschieden" und ist kein Mangel: dann gilt die Vermutung aus der Kategorie.
/// Ein Standardwert waere hier falsch - er saehe aus wie eine Entscheidung, und niemand wuesste
/// spaeter, ob sie jemand getroffen hat.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260922180000_PropertyImprovementTreatment")]
public sealed class PropertyImprovementTreatment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
ALTER TABLE "PropertyImprovements"
  ADD COLUMN IF NOT EXISTS "Treatment" character varying(24) NULL;
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""ALTER TABLE "PropertyImprovements" DROP COLUMN IF EXISTS "Treatment";""");
}
