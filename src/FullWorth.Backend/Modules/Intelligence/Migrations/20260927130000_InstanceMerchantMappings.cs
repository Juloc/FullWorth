using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Die instanzweite Haendler-zu-Kategorie-Zuordnung heisst jetzt nach dem, was sie ist, und
/// verliert vier Spalten, die niemand mehr liest.
///
/// <c>OfficialMerchantMappings</c> hiess nach dem Wissenspaket, das ihre Zeilen einmal fuellte -
/// die Cloud ist seit dem 2026-09-26 abgeschafft, die Tabelle nicht: ihre Rolle ist geblieben,
/// nur ihr Schreiber hat gewechselt (siehe <c>20260926120000_RemoveFullWorthCloud</c>, die die
/// Zeilen bereits geleert hat). <c>CanonicalMerchantKey</c>, <c>CanonicalName</c>, <c>Domain</c>
/// und <c>LogoKey</c> trugen die Markenidentitaet eines Haendlers mit, weil das Wissenspaket beides
/// in einer Zeile lieferte - diese Identitaet lebt seitdem vollstaendig in den Marken-Tabellen
/// (<c>ResearchedBrandAlias</c> und Verwandte); keine der vier Spalten hatte hier noch einen Leser.
///
/// Die Tabelle ist leer (seit der Cloud-Abschaffung), deshalb reicht ein einfaches
/// <c>ALTER TABLE ... RENAME</c> ohne Datenwanderung.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260927130000_InstanceMerchantMappings")]
public sealed class InstanceMerchantMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "OfficialMerchantMappings" RENAME TO "InstanceMerchantMappings";
ALTER INDEX IF EXISTS "IX_OfficialMerchantMappings_AliasKey_Direction_Country"
    RENAME TO "IX_InstanceMerchantMappings_AliasKey_Direction_Country";
DROP INDEX IF EXISTS "IX_OfficialMerchantMappings_CanonicalMerchantKey";

ALTER TABLE "InstanceMerchantMappings" DROP COLUMN IF EXISTS "CanonicalMerchantKey";
ALTER TABLE "InstanceMerchantMappings" DROP COLUMN IF EXISTS "CanonicalName";
ALTER TABLE "InstanceMerchantMappings" DROP COLUMN IF EXISTS "Domain";
ALTER TABLE "InstanceMerchantMappings" DROP COLUMN IF EXISTS "LogoKey";
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "InstanceMerchantMappings" ADD COLUMN IF NOT EXISTS "CanonicalMerchantKey" character varying(180) NOT NULL DEFAULT '';
ALTER TABLE "InstanceMerchantMappings" ADD COLUMN IF NOT EXISTS "CanonicalName" character varying(240) NOT NULL DEFAULT '';
ALTER TABLE "InstanceMerchantMappings" ADD COLUMN IF NOT EXISTS "Domain" character varying(255) NULL;
ALTER TABLE "InstanceMerchantMappings" ADD COLUMN IF NOT EXISTS "LogoKey" character varying(180) NULL;

CREATE INDEX IF NOT EXISTS "IX_OfficialMerchantMappings_CanonicalMerchantKey"
    ON "InstanceMerchantMappings" ("CanonicalMerchantKey");
ALTER INDEX IF EXISTS "IX_InstanceMerchantMappings_AliasKey_Direction_Country"
    RENAME TO "IX_OfficialMerchantMappings_AliasKey_Direction_Country";
ALTER TABLE "InstanceMerchantMappings" RENAME TO "OfficialMerchantMappings";
""");
    }
}
