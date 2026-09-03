using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260901221500_AiInstanceSettingsSingleton")]
public sealed class AiInstanceSettingsSingletonMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ScopeKey",
            table: "AiInstanceSettings",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "instance");

        // Older feature builds could race and create more than one instance settings row. Keep the
        // most recently updated row before enforcing the singleton key. There are no dependent FKs
        // to AiInstanceSettings, so removing stale duplicate configuration rows is safe.
        migrationBuilder.Sql("""
WITH ranked AS (
    SELECT "Id",
           ROW_NUMBER() OVER (PARTITION BY "ScopeKey" ORDER BY "UpdatedAt" DESC, "Id" DESC) AS rn
    FROM "AiInstanceSettings"
)
DELETE FROM "AiInstanceSettings" s
USING ranked r
WHERE s."Id" = r."Id" AND r.rn > 1;
""");

        migrationBuilder.CreateIndex(
            name: "IX_AiInstanceSettings_ScopeKey",
            table: "AiInstanceSettings",
            column: "ScopeKey",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AiInstanceSettings_ScopeKey",
            table: "AiInstanceSettings");

        migrationBuilder.DropColumn(
            name: "ScopeKey",
            table: "AiInstanceSettings");
    }
}
