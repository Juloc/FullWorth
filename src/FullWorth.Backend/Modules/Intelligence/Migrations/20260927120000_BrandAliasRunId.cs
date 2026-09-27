using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Eine von der KI vorgeschlagene Markenzuordnung bekommt ihren Beleg.
///
/// Ohne <c>RunId</c> sieht man einer Zeile mit <c>Source = 'ai'</c> nicht an, welches Modell,
/// welcher Anbieter und welcher Tag sie geschrieben hat - und findet eine falsche Charge nie
/// gezielt, nur durch Zufall beim Durchsuchen aller Zeilen.
///
/// <c>SET NULL</c> statt Kaskade: der KI-Lauf ist ein Protokolleintrag, die Markenzuordnung ist
/// Wissen. Wird der Protokolleintrag irgendwann geraeumt - zum Beispiel weil sein Benutzer
/// geloescht wird -, verliert die Zeile ihren Beleg, nicht ihr Logo.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260927120000_BrandAliasRunId")]
public sealed class BrandAliasRunId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ResearchedBrandAliases" ADD COLUMN IF NOT EXISTS "RunId" uuid NULL;
CREATE INDEX IF NOT EXISTS "IX_ResearchedBrandAliases_RunId" ON "ResearchedBrandAliases" ("RunId");
ALTER TABLE "ResearchedBrandAliases" DROP CONSTRAINT IF EXISTS "FK_ResearchedBrandAliases_AiRuns_RunId";
ALTER TABLE "ResearchedBrandAliases" ADD CONSTRAINT "FK_ResearchedBrandAliases_AiRuns_RunId"
    FOREIGN KEY ("RunId") REFERENCES "AiRuns" ("Id") ON DELETE SET NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ResearchedBrandAliases" DROP CONSTRAINT IF EXISTS "FK_ResearchedBrandAliases_AiRuns_RunId";
DROP INDEX IF EXISTS "IX_ResearchedBrandAliases_RunId";
ALTER TABLE "ResearchedBrandAliases" DROP COLUMN IF EXISTS "RunId";
""");
    }
}
