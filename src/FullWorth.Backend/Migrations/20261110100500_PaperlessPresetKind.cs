using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20261110100500_PaperlessPresetKind")]
public partial class PaperlessPresetKind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Wofuer ein Preset Dokumente holt: 'receipt' (alle bisherigen) oder 'payslip'. Text statt
        // Enum, damit eine weitere Art keine Migration der Spalte braucht.
        //
        // Der Name ist ab jetzt je Benutzer eindeutig statt je Space. Der Dienst hat schon immer nur
        // gegen die eigenen Presets geprueft, der Index gegen alle des Spaces: zwei Mitglieder, die ihr
        // Preset gleich nannten, bekamen beim Speichern einen 500. Mit Lohnabrechnungs-Presets, die fast
        // jeder "Lohnabrechnungen" nennt, waere das der Normalfall.
        migrationBuilder.Sql("""
            ALTER TABLE "PaperlessImportPresets"
                ADD COLUMN IF NOT EXISTS "Kind" character varying(32) NOT NULL DEFAULT 'receipt';
            DROP INDEX IF EXISTS "IX_PaperlessImportPresets_Space_Name";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaperlessImportPresets_Space_User_Name"
                ON "PaperlessImportPresets" ("FullWorthSpaceId", "UserId", lower("Name"));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            ALTER TABLE "PaperlessImportPresets" DROP COLUMN IF EXISTS "Kind";
            DROP INDEX IF EXISTS "IX_PaperlessImportPresets_Space_User_Name";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaperlessImportPresets_Space_Name"
                ON "PaperlessImportPresets" ("FullWorthSpaceId", lower("Name"));
            """);
}
