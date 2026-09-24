using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Ein Importauftrag kann die gelesenen Quellzeilen bis zum Festschreiben aufheben (#131, Schritt 4).
///
/// Der Finanzguru-Weg war der letzte Dateiimport ohne Vorschau: hochladen hiess festschreiben. Damit
/// dazwischen eine Ansicht passt, in der man Zeilen abwaehlen kann, muessen die gelesenen Zeilen den
/// Schritt ueberleben - sonst muesste die Datei ein zweites Mal hochgeladen werden, und genau das
/// verbietet der Ablauf der Issue ("Datei auswaehlen" steht dort einmal).
///
/// Warum am AUFTRAG und nicht je Zeile: die Rohwerte einer Finanzguru-Zeile umfassen ihre
/// Aufteilungen, und die gehoeren zur Elternzeile. Je Kandidat gespeichert stuenden sie mehrfach da.
///
/// Verschluesselt, weil es Kontobewegungen sind - dieselbe Behandlung wie <c>Transactions.RawJson</c>.
/// Und wieder geleert, sobald der Auftrag festgeschrieben oder abgebrochen ist: ein Zwischenstand,
/// den niemand mehr braucht, ist nur noch ein Datenbestand, der geschuetzt werden muss.
/// <c>ImportStagingCleanupService</c> raeumt den Rest nach seiner Frist mit weg.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260924180000_FinanzguruStagingPayload")]
public sealed class FinanzguruStagingPayload : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "ImportJobs" ADD COLUMN IF NOT EXISTS "SourcePayloadEncrypted" text NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE "ImportJobs" DROP COLUMN IF EXISTS "SourcePayloadEncrypted";
        """);
}
