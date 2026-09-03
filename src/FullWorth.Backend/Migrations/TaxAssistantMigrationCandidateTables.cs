using Microsoft.EntityFrameworkCore.Migrations;

namespace FullWorth.Backend.Migrations;

internal static class TaxAssistantMigrationCandidateTables
{
    internal static void Create(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TaxCandidates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxYear = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                TaxCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                GrossAmount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                EligibleAmount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                EligiblePercentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                DetectionSource = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ReasonCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Explanation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                RuleVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaxCandidates", x => x.Id);
                table.ForeignKey("FK_TaxCandidates_FullWorthSpaces_FullWorthSpaceId", x => x.FullWorthSpaceId, "FullWorthSpaces", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_TaxCandidates_TaxCategories_TaxCategoryId", x => x.TaxCategoryId, "TaxCategories", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_TaxCandidates_TaxProfiles_TaxProfileId", x => x.TaxProfileId, "TaxProfiles", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_TaxCandidates_Users_ReviewedByUserId", x => x.ReviewedByUserId, "Users", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "TaxAnalysisRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxYear = table.Column<int>(type: "integer", nullable: false),
                Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RuleVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                SourcesAnalyzed = table.Column<int>(type: "integer", nullable: false),
                CandidatesCreated = table.Column<int>(type: "integer", nullable: false),
                CandidatesChanged = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaxAnalysisRuns", x => x.Id);
                table.ForeignKey("FK_TaxAnalysisRuns_FullWorthSpaces_FullWorthSpaceId", x => x.FullWorthSpaceId, "FullWorthSpaces", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_TaxAnalysisRuns_TaxProfiles_TaxProfileId", x => x.TaxProfileId, "TaxProfiles", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TaxCandidateSources",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TaxCandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaxCandidateSources", x => x.Id);
                table.ForeignKey("FK_TaxCandidateSources_TaxCandidates_TaxCandidateId", x => x.TaxCandidateId, "TaxCandidates", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TaxFeedback",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxCandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                OriginalStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OriginalCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                NewCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                NewEligiblePercentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaxFeedback", x => x.Id);
                table.ForeignKey("FK_TaxFeedback_FullWorthSpaces_FullWorthSpaceId", x => x.FullWorthSpaceId, "FullWorthSpaces", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_TaxFeedback_TaxCandidates_TaxCandidateId", x => x.TaxCandidateId, "TaxCandidates", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TaxUserMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                TaxProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                MatchType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                MatchValue = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                TaxCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                EligiblePercentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedFromCandidateId = table.Column<Guid>(type: "uuid", nullable: true),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaxUserMappings", x => x.Id);
                table.ForeignKey("FK_TaxUserMappings_FullWorthSpaces_FullWorthSpaceId", x => x.FullWorthSpaceId, "FullWorthSpaces", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_TaxUserMappings_TaxCandidates_CreatedFromCandidateId", x => x.CreatedFromCandidateId, "TaxCandidates", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_TaxUserMappings_TaxCategories_TaxCategoryId", x => x.TaxCategoryId, "TaxCategories", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_TaxUserMappings_TaxProfiles_TaxProfileId", x => x.TaxProfileId, "TaxProfiles", "Id", onDelete: ReferentialAction.Cascade);
            });
    }
}
