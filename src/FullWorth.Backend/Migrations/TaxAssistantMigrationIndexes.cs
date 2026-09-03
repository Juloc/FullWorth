using Microsoft.EntityFrameworkCore.Migrations;

namespace FullWorth.Backend.Migrations;

internal static class TaxAssistantMigrationIndexes
{
    internal static void Create(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex("IX_TaxCategories_CountryCode_Code_ValidFromTaxYear", "TaxCategories", new[] { "CountryCode", "Code", "ValidFromTaxYear" }, unique: true);
        migrationBuilder.CreateIndex("IX_TaxRuleDefinitions_CountryCode_RuleCode_Version", "TaxRuleDefinitions", new[] { "CountryCode", "RuleCode", "Version" }, unique: true);
        migrationBuilder.CreateIndex("IX_TaxSettings_FullWorthSpaceId", "TaxSettings", "FullWorthSpaceId", unique: true);
        migrationBuilder.CreateIndex("IX_TaxProfiles_FullWorthSpaceId", "TaxProfiles", "FullWorthSpaceId");
        migrationBuilder.CreateIndex("IX_TaxProfiles_FullWorthSpaceId_UserId", "TaxProfiles", new[] { "FullWorthSpaceId", "UserId" }, unique: true);
        migrationBuilder.CreateIndex("IX_TaxProfiles_UserId", "TaxProfiles", "UserId");
        migrationBuilder.CreateIndex("IX_TaxCandidates_FullWorthSpaceId_TaxYear_Status", "TaxCandidates", new[] { "FullWorthSpaceId", "TaxYear", "Status" });
        migrationBuilder.CreateIndex("IX_TaxCandidates_ReviewedByUserId", "TaxCandidates", "ReviewedByUserId");
        migrationBuilder.CreateIndex("IX_TaxCandidates_TaxCategoryId", "TaxCandidates", "TaxCategoryId");
        migrationBuilder.CreateIndex("IX_TaxCandidates_TaxProfileId_TaxYear", "TaxCandidates", new[] { "TaxProfileId", "TaxYear" });
        migrationBuilder.CreateIndex("IX_TaxAnalysisRuns_FullWorthSpaceId_TaxYear_StartedAt", "TaxAnalysisRuns", new[] { "FullWorthSpaceId", "TaxYear", "StartedAt" });
        migrationBuilder.CreateIndex("IX_TaxAnalysisRuns_TaxProfileId", "TaxAnalysisRuns", "TaxProfileId");
        migrationBuilder.CreateIndex("IX_TaxCandidateSources_SourceType_SourceId", "TaxCandidateSources", new[] { "SourceType", "SourceId" });
        migrationBuilder.CreateIndex("IX_TaxCandidateSources_TaxCandidateId_SourceType_SourceId", "TaxCandidateSources", new[] { "TaxCandidateId", "SourceType", "SourceId" }, unique: true);
        migrationBuilder.CreateIndex("IX_TaxFeedback_FullWorthSpaceId_TaxCandidateId_CreatedAt", "TaxFeedback", new[] { "FullWorthSpaceId", "TaxCandidateId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_TaxFeedback_TaxCandidateId", "TaxFeedback", "TaxCandidateId");
        migrationBuilder.CreateIndex("IX_TaxUserMappings_CreatedFromCandidateId", "TaxUserMappings", "CreatedFromCandidateId");
        migrationBuilder.CreateIndex("IX_TaxUserMappings_FullWorthSpaceId_TaxProfileId_MatchType_MatchValue", "TaxUserMappings", new[] { "FullWorthSpaceId", "TaxProfileId", "MatchType", "MatchValue" });
        migrationBuilder.CreateIndex("IX_TaxUserMappings_TaxCategoryId", "TaxUserMappings", "TaxCategoryId");
        migrationBuilder.CreateIndex("IX_TaxUserMappings_TaxProfileId", "TaxUserMappings", "TaxProfileId");
    }
}
