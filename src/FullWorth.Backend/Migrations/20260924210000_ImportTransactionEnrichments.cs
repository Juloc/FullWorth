using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Was ein Import an einer Buchung ERGAENZT hat, die er nicht selbst angelegt hat (#131, Abschnitt 6/7).
///
/// Bisher kannte FullWorth nur "diese Buchung stammt aus diesem Import"
/// (<c>ImportTransactionLinks</c>, eine Buchung, ein Auftrag). Was fehlte, ist der zweite Fall aus dem
/// Issue: dieselbe reale Buchung kommt aus zwei Quellen. Die Bank liefert sie zuerst, Finanzguru
/// liefert sie noch einmal - dazu aber eine Kategorie, eine Aufteilung und die
/// Umbuchungskennzeichnung, die die Bank nicht hat. Der Import hat diese zweite Zeile bislang
/// gezaehlt und weggeworfen.
///
/// Eine zweite Zeile in <c>ImportTransactionLinks</c> waere der falsche Ort: die Tabelle beantwortet
/// "wer hat diese Buchung erzeugt, und wer darf sie beim Ruecknehmen loeschen", und darauf gibt es
/// genau eine Antwort (die UNIQUE-Bedingung auf "TransactionId" sagt das). Eine Ergaenzung erzeugt
/// nichts und darf beim Ruecknehmen nichts loeschen - sie muss genau ihren eigenen Beitrag
/// zuruecknehmen und die Buchung selbst stehen lassen.
///
/// Deshalb steht hier, WAS gesetzt wurde: die Kategorie, die der Import vergeben hat, und ob er die
/// Umbuchungskennzeichnung gesetzt hat. Die Aufteilungen brauchen keine Spalte - sie tragen ihre
/// Herkunft schon selbst ("TransactionAllocations.CreatedByImportJobId").
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260924210000_ImportTransactionEnrichments")]
public sealed class ImportTransactionEnrichments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE IF NOT EXISTS "ImportTransactionEnrichments" (
          "ImportJobId" uuid NOT NULL,
          "TransactionId" uuid NOT NULL,
          -- Die Kategorie, die DIESER Import gesetzt hat. Ohne Fremdschluessel: eine geloeschte
          -- Kategorie soll die Ruecknahme nicht blockieren, sie macht sie nur gegenstandslos.
          "SetCategoryId" uuid NULL,
          "SetTransfer" boolean NOT NULL DEFAULT false,
          "CreatedAt" timestamptz NOT NULL,
          CONSTRAINT "PK_ImportTransactionEnrichments" PRIMARY KEY ("ImportJobId","TransactionId"),
          CONSTRAINT "FK_ImportTransactionEnrichments_ImportJobs" FOREIGN KEY ("ImportJobId")
            REFERENCES "ImportJobs" ("Id") ON DELETE CASCADE,
          -- Verschwindet die Buchung, verschwindet die Notiz ueber sie mit. Sie beschreibt nichts mehr.
          CONSTRAINT "FK_ImportTransactionEnrichments_Transactions" FOREIGN KEY ("TransactionId")
            REFERENCES "Transactions" ("Id") ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS "IX_ImportTransactionEnrichments_Transaction"
          ON "ImportTransactionEnrichments" ("TransactionId");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE IF EXISTS "ImportTransactionEnrichments";
        """);
}
