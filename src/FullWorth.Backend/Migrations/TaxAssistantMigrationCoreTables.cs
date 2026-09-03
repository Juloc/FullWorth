using Microsoft.EntityFrameworkCore.Migrations;

namespace FullWorth.Backend.Migrations;

internal static class TaxAssistantMigrationCoreTables
{
    internal static void Create(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TaxCategories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                Code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                ParentCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                ValidFromTaxYear = table.Column<int>(type: "integer", nullable: false),
                ValidUntilTaxYear = table.Column<int>(type: "integer", nullable: true),
                Active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_TaxCategories", x => x.Id));

        migrationBuilder.CreateTable(
            name: "TaxRuleDefinitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                TaxYearFrom = table.Column<int>(type: "integer", nullable: false),
                TaxYearTo = table.Column<int>(type: "integer", nullable: true),
                RuleCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                RuleType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                Version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_TaxRuleDefinitions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "TaxSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                DefaultTaxYear = table.Column<int>(type: "integer", nullable: false),
                AutomaticAnalysisEnabled = table.Column<bool>(type: "boolean", nullable: false),
                AiAnalysisEnabled = table.Column<bool>(type: "boolean", nullable: false),
                AnalyzeTransactions = table.Column<bool>(type: "boolean", nullable: false),
                AnalyzePurchases = table.Column<bool>(type: "boolean", nullable: false),
                AnalyzeDocuments = table.Column<bool>(type: "boolean", nullable: false),
                ShowTaxNotifications = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaxSettings", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaxSettings_FullWorthSpaces_FullWorthSpaceId",
                    column: x => x.FullWorthSpaceId,
                    principalTable: "FullWorthSpaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "TaxProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                AssistantEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaxProfiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaxProfiles_FullWorthSpaces_FullWorthSpaceId",
                    column: x => x.FullWorthSpaceId,
                    principalTable: "FullWorthSpaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_TaxProfiles_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });
    }
}
