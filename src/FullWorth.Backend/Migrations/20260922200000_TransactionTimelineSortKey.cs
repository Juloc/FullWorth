using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Die Sortierreihenfolge der Timeline als EINE indexierbare Spalte (#161).
///
/// Gemessen an 200 000 Buchungen auf diesem Rechner, gleiche Sortierung, Seite 2000:
///
/// <code>
///   OFFSET 100000            80 ms   external merge, 2992 kB auf Platte, 100 051 Zeilen erzeugt
///   + COUNT je Seite        +17 ms   bei JEDEM Nachladen
///   Cursor als OR-Kette      24 ms   top-N heapsort, aber kein Index nutzbar
///   Cursor auf dieser Spalte  0,3 ms  reiner Index-Bereichsscan, 55 Puffer
/// </code>
///
/// Der Unterschied zwischen den letzten beiden ist der Grund fuer diese Migration. Eine
/// Cursor-Bedingung ueber vier Spalten laesst sich nur als Zeilenwertvergleich
/// (<c>ROW(...) &lt; ROW(...)</c>) in einen Index-Bereich uebersetzen, und den kann EF nicht
/// erzeugen. Ein Vergleich auf EINER Spalte kann es.
///
/// Der Schluessel bildet das bestehende Tupel exakt ab, damit sich die Reihenfolge nicht aendert:
///
/// <code>
///   vorgemerkt?   '1'/'0'    absteigend: vorgemerkt zuerst - wie bisher
///   Datum         8 Stellen  Tagesnummer; ohne Datum 9999-12-31, also dort, wo PostgreSQL
///                            NULL bei DESC ohnehin hinsortiert
///   UpdatedAt    20 Stellen  Mikrosekunden seit 1970, fuehrende Nullen -> lexikografisch = numerisch
///   Id           32 Stellen  Hex ohne Bindestriche; dieselbe Ordnung wie der uuid-Vergleich
/// </code>
///
/// <b>Warum die Id ueberhaupt dazugehoert:</b> keines der ersten drei Felder ist eindeutig. Ein
/// Import legt dutzende Buchungen mit demselben Datum und demselben Zeitstempel an; zwischen ihnen
/// war die Reihenfolge bei jeder Abfrage neu beliebig, und dann ueberspringt ein Seitenwechsel Zeilen
/// oder zeigt sie zweimal. Das galt schon fuer das bisherige Skip/Take.
///
/// <b>GENERATED ... STORED und kein Anwendungscode:</b> ein von Hand gepflegter Schluessel waere eine
/// zweite Wahrheit ueber die Reihenfolge, die beim naechsten Schreibpfad vergessen wird. Die
/// Datenbank rechnet ihn aus denselben Spalten, aus denen auch das ORDER BY kommt.
///
/// Das ALTER schreibt die Tabelle einmal neu.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260922200000_TransactionTimelineSortKey")]
public sealed class TransactionTimelineSortKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
ALTER TABLE "Transactions" ADD COLUMN IF NOT EXISTS "TimelineSortKey" text
  GENERATED ALWAYS AS (
    (CASE WHEN "Status" = 'PDNG' THEN '1' ELSE '0' END)
    || lpad((COALESCE("BookingDate","ValueDate", DATE '9999-12-31') - DATE '0001-01-01')::text, 8, '0')
    -- GREATEST gegen einen Zeitstempel vor 1970: ein negativer Wert bekaeme ein Minuszeichen und
    -- sortierte damit vor allem anderen. Er kommt nicht vor - aber eine Sortierung, die bei einer
    -- kaputten Zeile still umkippt, faellt niemandem auf.
    || lpad(GREATEST((extract(epoch from ("UpdatedAt" - TIMESTAMPTZ '1970-01-01 00:00:00+00')) * 1000000)::bigint, 0)::text, 20, '0')
    || replace("Id"::text, '-', '')
  ) STORED;

-- Der Bereichsscan der Timeline: Konto zuerst, dann die Reihenfolge. Genau die Form, die gemessen
-- 0,3 ms statt 80 ms gebraucht hat.
CREATE INDEX IF NOT EXISTS "IX_Transactions_AccountId_TimelineSortKey"
  ON "Transactions" ("AccountId", "TimelineSortKey" DESC);
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_Transactions_AccountId_TimelineSortKey";
ALTER TABLE "Transactions" DROP COLUMN IF EXISTS "TimelineSortKey";
""");
}
