using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Haelt fest, in welcher Sprache die Standardkategorien eines Space angelegt wurden - und DASS sie
/// angelegt wurden.
///
/// Der zweite Teil ist der wichtigere. Der Seeder lief bisher bei JEDEM Start fuer JEDEN Space und
/// legte jeden Standardschluessel an, der gerade fehlte. Wer eine Standardkategorie loeschte - ueber
/// das Zusammenfuehren mit "Quelle loeschen" geht das - hatte sie nach dem naechsten Neustart wieder,
/// und zwar in der urspruenglichen Sprache. Zusammen mit Kategorien, die ein Import auf Deutsch
/// angelegt hat, ist das genau das gemischte Set aus #117.
///
/// Der Nachtrag unten setzt den Marker fuer jeden Space, der schon Systemkategorien hat: dort ist
/// gesaet worden, auch wenn es damals niemand aufgeschrieben hat. Die Sprache wird dabei aus den
/// vorhandenen Namen erschlossen, nicht geraten - "Lebensmittel" heisst Deutsch, "Groceries"
/// Englisch. Ein Space ohne Systemkategorien bleibt ungesaet und bekommt sein Set beim naechsten
/// Start; das ist der Zustand einer frischen Installation.
///
/// Es wird nichts geloescht und nichts umbenannt. Was ein Benutzer geaendert hat, bleibt.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260915140000_DefaultCategoryLanguage")]
public sealed class DefaultCategoryLanguage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "FullWorthSpaces"
  ADD COLUMN IF NOT EXISTS "DefaultCategoryLanguage" character varying(8),
  ADD COLUMN IF NOT EXISTS "DefaultCategoriesSeededAt" timestamp with time zone;
""");

        // "food.groceries" traegt im deutschen Set "Lebensmittel", im englischen "Groceries". Ein Space,
        // dessen Benutzer die Kategorie umbenannt hat, faellt in keinen der beiden Faelle und bekommt
        // Englisch - das war bis heute die einzige Sprache, in der gesaet wurde.
        migrationBuilder.Sql("""
UPDATE "FullWorthSpaces" s
SET "DefaultCategoriesSeededAt" = now(),
    "DefaultCategoryLanguage" = CASE
      WHEN EXISTS (
        SELECT 1 FROM "Categories" c
        WHERE c."FullWorthSpaceId" = s."Id" AND c."IsSystem" AND c."Key" = 'food.groceries'
          AND c."Name" = 'Lebensmittel')
      THEN 'de' ELSE 'en' END
WHERE s."DefaultCategoriesSeededAt" IS NULL
  AND EXISTS (SELECT 1 FROM "Categories" c WHERE c."FullWorthSpaceId" = s."Id" AND c."IsSystem");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
ALTER TABLE "FullWorthSpaces"
  DROP COLUMN IF EXISTS "DefaultCategoryLanguage",
  DROP COLUMN IF EXISTS "DefaultCategoriesSeededAt";
""");
}
