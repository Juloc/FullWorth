using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Welches Konto eine Kontobezeichnung aus einer Importdatei meint (#131, Abschnitt 3:
/// "einmal bestaetigte Zuordnungen werden fuer spaetere Importe wiederverwendet").
///
/// Der Finanzguru-Weg merkt sich das seit #112 ueber
/// <c>Accounts.ImportLinkedAccountId</c> - aber nur, weil er fuer jede Quelle ein eigenes
/// Importkonto anlegt, an das sich die Verknuepfung haengen laesst. Der allgemeine CSV/XLSX-Weg hat
/// kein solches Konto und merkte sich deshalb gar nichts: wer denselben Bankexport zum zweiten Mal
/// hochlud, ordnete "C24 Girokonto" wieder von Hand zu, und beim dritten Mal auch.
///
/// Der Schluessel ist der Raum und die Bezeichnung, nicht der Adapter: "Girokonto" aus einem
/// C24-Export und aus einem Finanzfluss-Export meint dasselbe Konto, und eine Zuordnung je Adapter zu
/// fuehren hiesse, dieselbe Frage pro Dateiformat neu zu stellen.
///
/// Die Bezeichnung steht normalisiert da (getrimmt, kleingeschrieben) - sonst waeren
/// "C24 Girokonto" und "c24 girokonto" zwei Eintraege, von denen der Nutzer einen nie gemacht hat.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260925090000_ImportSourceAccountLinks")]
public sealed class ImportSourceAccountLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE IF NOT EXISTS "ImportSourceAccountLinks" (
          "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE,
          "SourceKey" text NOT NULL,
          -- Verschwindet das Konto, verschwindet die Zuordnung darauf. Eine Erinnerung an ein Konto,
          -- das es nicht mehr gibt, wuerde beim naechsten Import ins Leere zeigen.
          "AccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE CASCADE,
          "UpdatedAt" timestamptz NOT NULL,
          CONSTRAINT "PK_ImportSourceAccountLinks" PRIMARY KEY ("FullWorthSpaceId","SourceKey")
        );

        CREATE INDEX IF NOT EXISTS "IX_ImportSourceAccountLinks_Account"
          ON "ImportSourceAccountLinks" ("AccountId");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE IF EXISTS "ImportSourceAccountLinks";
        """);
}
