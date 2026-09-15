using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Macht aus einem Etikett eine Sammlung: Symbol, Beschreibung, Zeitraum, Status (#124).
///
/// Es entsteht KEINE zweite Tabelle. FinanceTags und TransactionTags tragen die n:m-Beziehung
/// zwischen Buchung und Sammlung bereits - mit zusammengesetztem Schluessel und ON CONFLICT DO
/// NOTHING, also ohne Doppelzuordnung. Was fehlte, waren die fuenf Felder, die eine Reise oder ein
/// Projekt von einem blossen Wort unterscheiden.
///
/// Status ist NOT NULL mit Vorgabe "active": eine bestehende Zeile ist eine laufende Sammlung, und
/// nichts anderes waere wahr.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260915170000_Collections")]
public sealed class Collections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "FinanceTags"
  ADD COLUMN IF NOT EXISTS "Icon" character varying(64),
  ADD COLUMN IF NOT EXISTS "Description" character varying(500),
  ADD COLUMN IF NOT EXISTS "StartDate" date,
  ADD COLUMN IF NOT EXISTS "EndDate" date,
  ADD COLUMN IF NOT EXISTS "Status" character varying(16) NOT NULL DEFAULT 'active';
""");

        // Nur die drei bekannten Zustaende. Ein vierter waere ein Zustand, den keine Auswertung kennt.
        migrationBuilder.Sql("""
ALTER TABLE "FinanceTags" DROP CONSTRAINT IF EXISTS "CK_FinanceTags_Status";
ALTER TABLE "FinanceTags"
  ADD CONSTRAINT "CK_FinanceTags_Status" CHECK ("Status" IN ('active','completed','archived'));
""");

        // Ein Zeitraum, der rueckwaerts laeuft, ist kein Zeitraum.
        migrationBuilder.Sql("""
ALTER TABLE "FinanceTags" DROP CONSTRAINT IF EXISTS "CK_FinanceTags_Period";
ALTER TABLE "FinanceTags"
  ADD CONSTRAINT "CK_FinanceTags_Period"
  CHECK ("StartDate" IS NULL OR "EndDate" IS NULL OR "EndDate" >= "StartDate");
""");

        // Die Uebersicht sortiert nach Status und Name; ohne diesen Index liest sie die ganze Tabelle.
        migrationBuilder.Sql("""
CREATE INDEX IF NOT EXISTS "IX_FinanceTags_FullWorthSpaceId_Status"
  ON "FinanceTags" ("FullWorthSpaceId", "Status");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_FinanceTags_FullWorthSpaceId_Status";
ALTER TABLE "FinanceTags" DROP CONSTRAINT IF EXISTS "CK_FinanceTags_Period";
ALTER TABLE "FinanceTags" DROP CONSTRAINT IF EXISTS "CK_FinanceTags_Status";
ALTER TABLE "FinanceTags"
  DROP COLUMN IF EXISTS "Icon",
  DROP COLUMN IF EXISTS "Description",
  DROP COLUMN IF EXISTS "StartDate",
  DROP COLUMN IF EXISTS "EndDate",
  DROP COLUMN IF EXISTS "Status";
""");
}
