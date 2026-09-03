using Microsoft.EntityFrameworkCore.Migrations;

namespace FullWorth.Backend.Migrations;

internal static class TaxAssistantMigrationSchema
{
    internal static void Up(MigrationBuilder migrationBuilder)
    {
        TaxAssistantMigrationCoreTables.Create(migrationBuilder);
        TaxAssistantMigrationCandidateTables.Create(migrationBuilder);
        TaxAssistantMigrationIndexes.Create(migrationBuilder);
    }

    internal static void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TaxAnalysisRuns");
        migrationBuilder.DropTable(name: "TaxCandidateSources");
        migrationBuilder.DropTable(name: "TaxFeedback");
        migrationBuilder.DropTable(name: "TaxRuleDefinitions");
        migrationBuilder.DropTable(name: "TaxSettings");
        migrationBuilder.DropTable(name: "TaxUserMappings");
        migrationBuilder.DropTable(name: "TaxCandidates");
        migrationBuilder.DropTable(name: "TaxCategories");
        migrationBuilder.DropTable(name: "TaxProfiles");
    }
}
