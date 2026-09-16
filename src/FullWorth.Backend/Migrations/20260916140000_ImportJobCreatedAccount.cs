using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Haelt fest, dass ein Import sein Zielkonto selbst angelegt hat - damit die Ruecknahme es wieder
/// mitnehmen kann, wenn danach nichts mehr darin steht.
///
/// Ohne diese Spalte blieb nach einem Rollback ein leeres Konto stehen, das niemand bestellt hatte:
/// der Nutzer nimmt den Import zurueck, um ihn ungeschehen zu machen, und findet danach ein Konto
/// ohne Buchungen, das sich nur archivieren laesst.
///
/// Der Fremdschluessel steht auf SET NULL: wer das Konto anders loescht, soll nicht am Auftrag
/// haengenbleiben.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260916140000_ImportJobCreatedAccount")]
public sealed class ImportJobCreatedAccount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "ImportJobs"
  ADD COLUMN IF NOT EXISTS "CreatedAccountId" uuid NULL;

ALTER TABLE "ImportJobs"
  DROP CONSTRAINT IF EXISTS "FK_ImportJobs_Accounts_CreatedAccountId";
ALTER TABLE "ImportJobs"
  ADD CONSTRAINT "FK_ImportJobs_Accounts_CreatedAccountId"
  FOREIGN KEY ("CreatedAccountId") REFERENCES "Accounts" ("Id") ON DELETE SET NULL;
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "ImportJobs"
  DROP CONSTRAINT IF EXISTS "FK_ImportJobs_Accounts_CreatedAccountId";
ALTER TABLE "ImportJobs"
  DROP COLUMN IF EXISTS "CreatedAccountId";
""");
}
