using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Die Buchungsliste sortiert nach Zahltag, und "vorgemerkt" ist kein Sortierkriterium mehr.
///
/// Bisher stand vorne im Schluessel ein Zeichen "vorgemerkt?": jede vorgemerkte Zeile kam vor jeder
/// gebuchten, egal wie alt sie war. Das war als Trennlinie "gebucht / noch nicht gebucht" gedacht und
/// hat die Liste falschherum gezeigt - der Zukunftsblock und die Vorgemerkten oben, "Heute" darunter.
/// Eine vorgemerkte Karte hat aber schon ihren Zahltag (die Bank liefert ihn als Buchungsdatum), also
/// gehoert sie dorthin und wird mit einem Hinweis gekennzeichnet statt an eine eigene Stelle gesetzt.
///
/// Das Datum faellt auf den Tag der ersten Beobachtung zurueck statt auf 9999-12-31: eine Zeile ganz
/// ohne Datum stand vorher als "unendlich in der Zukunft" ganz oben und tut es nicht mehr.
///
/// Der Rest des Schluessels bleibt - 8 Stellen Datum, 20 Stellen UpdatedAt, 32 Stellen Id -, damit der
/// Cursor weiter eine einzige Spalte vergleicht und aus der Bedingung ein Index-Bereich wird (#161).
///
/// Die Spalte wird neu angelegt statt mit SET EXPRESSION geaendert: das gibt es erst ab PostgreSQL 17,
/// und der Server muss nicht dort stehen. Das Neuanlegen schreibt die Tabelle einmal neu - wie schon
/// beim ersten Anlegen. Beide Indizes gehen mit der Spalte und werden neu erzeugt.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20261106100000_TimelineSortByPayDate")]
public sealed class TimelineSortByPayDate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
ALTER TABLE "Transactions" DROP COLUMN IF EXISTS "TimelineSortKey";

ALTER TABLE "Transactions" ADD COLUMN "TimelineSortKey" text
  GENERATED ALWAYS AS (
    lpad((COALESCE("BookingDate", "ValueDate", ("FirstSeenAt" AT TIME ZONE 'UTC')::date) - DATE '0001-01-01')::text, 8, '0')
    || lpad(GREATEST((extract(epoch from ("UpdatedAt" - TIMESTAMPTZ '1970-01-01 00:00:00+00')) * 1000000)::bigint, 0)::text, 20, '0')
    || replace("Id"::text, '-', '')
  ) STORED;

CREATE INDEX "IX_Transactions_AccountId_TimelineSortKey" ON "Transactions" ("AccountId", "TimelineSortKey" DESC);
CREATE INDEX "IX_Transactions_TimelineSortKey" ON "Transactions" ("TimelineSortKey" DESC);
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
ALTER TABLE "Transactions" DROP COLUMN IF EXISTS "TimelineSortKey";

ALTER TABLE "Transactions" ADD COLUMN "TimelineSortKey" text
  GENERATED ALWAYS AS (
    (CASE WHEN "Status" = 'PDNG' THEN '1' ELSE '0' END)
    || lpad((COALESCE("BookingDate","ValueDate", DATE '9999-12-31') - DATE '0001-01-01')::text, 8, '0')
    || lpad(GREATEST((extract(epoch from ("UpdatedAt" - TIMESTAMPTZ '1970-01-01 00:00:00+00')) * 1000000)::bigint, 0)::text, 20, '0')
    || replace("Id"::text, '-', '')
  ) STORED;

CREATE INDEX "IX_Transactions_AccountId_TimelineSortKey" ON "Transactions" ("AccountId", "TimelineSortKey" DESC);
CREATE INDEX "IX_Transactions_TimelineSortKey" ON "Transactions" ("TimelineSortKey" DESC);
""");
}
