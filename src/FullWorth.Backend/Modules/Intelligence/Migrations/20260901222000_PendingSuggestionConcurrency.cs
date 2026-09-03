using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260901222000_PendingSuggestionConcurrency")]
public sealed class PendingSuggestionConcurrencyMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Older workers could create the same pending FullWorthSpace suggestion concurrently. Keep the
        // oldest pending suggestion so existing review links remain stable, then enforce one pending
        // semantic suggestion per FullWorthSpace/subject. Reviewed rows remain unrestricted.
        migrationBuilder.Sql("""
WITH ranked AS (
    SELECT "Id",
           ROW_NUMBER() OVER (
               PARTITION BY "FullWorthSpaceId", "SubjectType", "SubjectId", "SemanticKey"
               ORDER BY "CreatedAt", "Id") AS rn
    FROM "IntelligenceSuggestions"
    WHERE "FullWorthSpaceId" IS NOT NULL AND "Status" = 'pending'
)
DELETE FROM "IntelligenceSuggestions" s
USING ranked r
WHERE s."Id" = r."Id" AND r.rn > 1;
""");

        migrationBuilder.CreateIndex(
            name: IntelligenceSuggestionConcurrencyConfiguration.PendingFullWorthSpaceIndexName,
            table: "IntelligenceSuggestions",
            columns: new[] { "FullWorthSpaceId", "SubjectType", "SubjectId", "SemanticKey" },
            unique: true,
            filter: "\"FullWorthSpaceId\" IS NOT NULL AND \"Status\" = 'pending'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: IntelligenceSuggestionConcurrencyConfiguration.PendingFullWorthSpaceIndexName,
            table: "IntelligenceSuggestions");
    }
}
