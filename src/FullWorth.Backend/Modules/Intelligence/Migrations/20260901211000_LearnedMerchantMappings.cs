using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260901211000_LearnedMerchantMappings")]
public sealed class LearnedMerchantMappingsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LearnedMerchantMappings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                NormalizedCounterparty = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LearnedMerchantMappings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LearnedMerchantMappings_FullWorthSpaceId_IsActive",
            table: "LearnedMerchantMappings",
            columns: new[] { "FullWorthSpaceId", "IsActive" });

        migrationBuilder.CreateIndex(
            name: "IX_LearnedMerchantMappings_FullWorthSpaceId_NormalizedCounterparty_Direction",
            table: "LearnedMerchantMappings",
            columns: new[] { "FullWorthSpaceId", "NormalizedCounterparty", "Direction" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "LearnedMerchantMappings");
    }
}
