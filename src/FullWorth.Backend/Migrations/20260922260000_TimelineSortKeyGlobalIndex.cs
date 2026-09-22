using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Der Index fuer die Standardsicht der Buchungsliste (#161).
///
/// Es gab schon einen auf ("AccountId", "TimelineSortKey" DESC) - der hilft, solange ein Konto
/// gewaehlt ist. Die Standardsicht zeigt aber ALLE Konten, und dafuer kann er nichts tun: gemessen an
/// 200 000 Buchungen war jede Seite dieser Sicht ein vollstaendiger Durchlauf mit anschliessender
/// Sortierung. 65 ms, bei jedem Nachladen, und der Cursor half nicht - er kann nur so schnell sein
/// wie die Sortierung, an der er haengt.
///
/// Mit diesem Index sind es 0,5 ms.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260922260000_TimelineSortKeyGlobalIndex")]
public sealed class TimelineSortKeyGlobalIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE INDEX IF NOT EXISTS "IX_Transactions_TimelineSortKey"
            ON "Transactions" ("TimelineSortKey" DESC);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP INDEX IF EXISTS "IX_Transactions_TimelineSortKey";
        """);
}
