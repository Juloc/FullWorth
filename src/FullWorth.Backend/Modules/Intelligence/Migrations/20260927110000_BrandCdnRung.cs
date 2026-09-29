using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Die dritte Sprosse der Logoleiter bekommt ihren Schalter und ihr eigenes Gedaechtnis.
///
/// <b><c>BrandCdnLookupEnabled</c></b> entscheidet, ob ein selbst ausgerechneter Marken-Kurzname
/// beim Icon-Spiegel nachgeschlagen werden darf. Vorgabe <c>true</c>, fuer bestehende Installationen
/// genauso wie fuer neue: die Entscheidung ist, dass eine Instanz immer zuerst selbst sucht, und
/// eine Vorgabe, die dem widerspricht, waere keine Vorsicht, sondern eine stille Abweichung von dem,
/// was in den Einstellungen steht.
///
/// <b><c>Rung</c></b> trennt die Vermerke der beiden bezahlten Sprossen. Der eindeutige Index lag
/// bisher auf dem Namens-Hash allein: ein "beim Spiegel nicht gefunden" haette damit dreissig Tage
/// lang auch die KI-Sprosse blockiert, die es vielleicht gekonnt haette. Bestehende Zeilen bekommen
/// <c>ai</c> - es gab nur diese eine Sprosse, als sie geschrieben wurden.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260927110000_BrandCdnRung")]
public sealed class BrandCdnRung : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "AiInstanceSettings" ADD COLUMN IF NOT EXISTS "BrandCdnLookupEnabled" boolean NOT NULL DEFAULT TRUE;

ALTER TABLE "BrandLogoResearchAttempts" ADD COLUMN IF NOT EXISTS "Rung" character varying(10) NOT NULL DEFAULT 'ai';
DROP INDEX IF EXISTS "IX_BrandLogoResearchAttempts_AliasHash";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BrandLogoResearchAttempts_AliasHash_Rung"
    ON "BrandLogoResearchAttempts" ("AliasHash", "Rung");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Zurueck auf einen Vermerk je Haendler: die Zeilen der CDN-Sprosse muessen weg, sonst
        // scheitert der eindeutige Index an genau den Paaren, die diese Migration erlaubt hat.
        migrationBuilder.Sql("""
DELETE FROM "BrandLogoResearchAttempts" WHERE "Rung" = 'cdn';
DROP INDEX IF EXISTS "IX_BrandLogoResearchAttempts_AliasHash_Rung";
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BrandLogoResearchAttempts_AliasHash"
    ON "BrandLogoResearchAttempts" ("AliasHash");
ALTER TABLE "BrandLogoResearchAttempts" DROP COLUMN IF EXISTS "Rung";

ALTER TABLE "AiInstanceSettings" DROP COLUMN IF EXISTS "BrandCdnLookupEnabled";
""");
    }
}
