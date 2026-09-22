using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Wortindex statt <c>%ILIKE%</c> ueber vier Spalten (#161).
///
/// Gemessen an 200 000 Buchungen auf diesem Rechner:
///
/// <code>
///   Suchbegriff ohne Treffer, ILIKE       156 ms   jede Zeile gelesen und verworfen
///   Suchbegriff ohne Treffer, Wortindex     0,14 ms
/// </code>
///
/// Der teure Fall ist der SELTENE Treffer, nicht der haeufige: bei "NETFLIX" liefert der
/// Timeline-Index nach 51 Zeilen genug, bei einem Tippfehler muss ohne Wortindex die ganze Tabelle
/// gelesen werden. Genau das passiert beim Tippen - jedes Zwischenergebnis einer Eingabe ist ein
/// Suchbegriff ohne Treffer.
///
/// <b>'simple' und nicht 'german'.</b> Ein Stemmer wuerde "Zahlung" und "zahlen" zusammenwerfen, aber
/// auch "REWE" und "Rewe-Markt" unterschiedlich zerlegen - und die Sprache steht nicht fest: in einem
/// Kontoauszug stehen deutsche Verwendungszwecke neben englischen Haendlernamen. 'simple' zerlegt nur
/// an Wortgrenzen und faltet Gross-/Kleinschreibung. Das entspricht dem, was die Suche vorher konnte,
/// und trifft keine Sprachannahme, die bei der Haelfte der Zeilen falsch waere.
///
/// <b>Was sich aendert:</b> gesucht wird ab Wortanfang statt irgendwo im Wort. "REW" findet weiterhin
/// "REWE" (Praefix), "EWE" nicht mehr. Das ist der Unterschied zwischen einer Wortsuche und einem
/// Textscan - und der Grund, warum die eine 0,14 ms und die andere 156 ms braucht.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260922220000_TransactionSearchVector")]
public sealed class TransactionSearchVector : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
ALTER TABLE "Transactions" ADD COLUMN IF NOT EXISTS "SearchVector" tsvector
  GENERATED ALWAYS AS (
    to_tsvector('simple',
      coalesce("Counterparty",'') || ' ' || coalesce("NormalizedCounterparty",'') || ' ' ||
      coalesce("Description",'')  || ' ' || coalesce("UserNote",''))
  ) STORED;

CREATE INDEX IF NOT EXISTS "IX_Transactions_SearchVector"
  ON "Transactions" USING GIN ("SearchVector");
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_Transactions_SearchVector";
ALTER TABLE "Transactions" DROP COLUMN IF EXISTS "SearchVector";
""");
}
