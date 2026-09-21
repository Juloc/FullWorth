using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Der Gesundheitszustand der Institute wird lokal gehalten (#165).
///
/// Bis hierher rief <c>GET /api/banking/provider-status</c> bei jedem Aufruf das
/// Enable-Banking-Control-Panel - Token holen, notfalls erneuern, <c>/api/get_today_stats</c> lesen,
/// bei 401 alles noch einmal. Der Bankdialog wartete darauf; ein langsames oder abgeschaltetes
/// Control Panel machte damit die Bankauswahl langsam oder unbenutzbar. Fuer einen Katalogzustand,
/// der sich taeglich einmal aendert, ist das der falsche Preis.
///
/// Zwei Tabellen, weil es zwei verschiedene Dinge sind: die Zeilen je Institut und der Zustand der
/// Aktualisierung. Der Feed kommt in einem Zug fuer alle Laender, also gibt es genau einen Zeitpunkt
/// und einen Fehlerzustand - je Institut wiederholt waere es dieselbe Angabe hundertfach.
///
/// Keine Nutzer- und keine Space-Spalte: der Feed gilt fuer die ganze Installation, nur die
/// Zugangsdaten zum Abruf gehoeren einem Nutzer.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260921090000_BankingProviderStatusCache")]
public sealed class BankingProviderStatusCache : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "BankingProviderStatuses" (
  "Id" uuid NOT NULL,
  "Country" character varying(2) NOT NULL,
  "Brand" character varying(200) NOT NULL,
  "PsuType" character varying(40) NOT NULL,
  "Status" character varying(60) NOT NULL,
  "UpdatedAt" timestamp with time zone NOT NULL,
  CONSTRAINT "PK_BankingProviderStatuses" PRIMARY KEY ("Id")
);
""");
        // Ein Institut je Land und PSU-Typ genau einmal. Ohne diesen Index haengt es an der
        // Einfuegereihenfolge, welcher von zwei Zustaenden derselben Bank gilt - und der Abgleich
        // unten braucht ihn, um aktualisieren statt anhaeufen zu koennen.
        migrationBuilder.Sql("""
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankingProviderStatuses_Aspsp"
  ON "BankingProviderStatuses" ("Country", "Brand", "PsuType");
""");
        // Die Bankauswahl liest immer nach Land - das ist der einzige Lesepfad, der zaehlt.
        migrationBuilder.Sql("""
CREATE INDEX IF NOT EXISTS "IX_BankingProviderStatuses_Country"
  ON "BankingProviderStatuses" ("Country");
""");

        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "BankingProviderStatusRefreshes" (
  "Id" uuid NOT NULL,
  "ScopeKey" character varying(40) NOT NULL,
  "LastAttemptAt" timestamp with time zone NULL,
  "LastSuccessfulAt" timestamp with time zone NULL,
  "LastError" character varying(200) NULL,
  CONSTRAINT "PK_BankingProviderStatusRefreshes" PRIMARY KEY ("Id")
);
""");
        migrationBuilder.Sql("""
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankingProviderStatusRefreshes_ScopeKey"
  ON "BankingProviderStatusRefreshes" ("ScopeKey");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Ein reiner Zwischenspeicher: wegwerfen kostet nichts, der Hintergrunddienst baut ihn
        // beim naechsten Durchlauf wieder auf.
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "BankingProviderStatusRefreshes";""");
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "BankingProviderStatuses";""");
    }
}
