using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Eine gueltige Importzeile, die dem Nutzer vorgelegt statt vorgewaehlt wird, bekommt ihren Grund
/// (#131, Abschnitt 11).
///
/// Ein PDF-Kontoauszug wird nachgerechnet: fuehren die gelesenen Buchungen nicht exakt vom Anfangs- zum
/// Endstand, ist mindestens ein Betrag falsch gelesen, und keine Zeile darf still uebernommen werden.
/// Das ist nicht dasselbe wie "ValidationError" - der macht eine Zeile unimportierbar. Eine Zeile mit
/// Pruefnotiz ist importierbar, nur eben nicht ungefragt.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260925150000_ImportCandidateReviewNote")]
public sealed class ImportCandidateReviewNote : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "ImportCandidates" ADD COLUMN IF NOT EXISTS "ReviewNote" text NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "ImportCandidates" DROP COLUMN IF EXISTS "ReviewNote";
        """);
}
