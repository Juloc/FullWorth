using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Die Standardgruppe (#125).
///
/// Ein Konto konnte bisher gar keiner Gruppe angehoeren, und die Kontenuebersicht zeigte dafuer einen
/// Eimer "Ohne Gruppe". Der sah aus wie eine Gruppe, war aber keine: eine Gruppenzeile oeffnet die
/// Buchungen genau ihrer Konten, und dafuer gibt es keinen Filter "hat keine Gruppe" - der Endpunkt
/// kennt nur <c>accountGroupId</c>.
///
/// Die Spalte macht aus dem Eimer eine echte Gruppe. Angelegt wird sie nicht hier, sondern beim
/// ersten Lesen der Gruppenliste: welchen Namen sie traegt, haengt an der Sprache des Space, und die
/// steht in einer anderen Tabelle. Eine Migration, die "Standard" auf Englisch in eine deutsche
/// Installation schreibt, waere der falsche Ort dafuer.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260915200000_AccountGroupDefault")]
public sealed class AccountGroupDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "AccountGroups" ADD COLUMN IF NOT EXISTS "IsDefault" boolean NOT NULL DEFAULT false;
""");

        migrationBuilder.Sql("""
CREATE INDEX IF NOT EXISTS "IX_AccountGroups_FullWorthSpaceId_IsDefault"
ON "AccountGroups" ("FullWorthSpaceId", "IsDefault");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_AccountGroups_FullWorthSpaceId_IsDefault";""");
        migrationBuilder.Sql("""ALTER TABLE "AccountGroups" DROP COLUMN IF EXISTS "IsDefault";""");
    }
}
