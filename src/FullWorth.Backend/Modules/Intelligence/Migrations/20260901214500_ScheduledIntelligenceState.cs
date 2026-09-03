using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260901214500_ScheduledIntelligenceState")]
public sealed class ScheduledIntelligenceStateMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IntelligenceWatermarks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Value = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_IntelligenceWatermarks", x => x.Id));

        migrationBuilder.CreateTable(
            name: "IntelligenceJobLeases",
            columns: table => new
            {
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                LeaseOwner = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IntelligenceJobLeases", x => x.JobId);
                table.ForeignKey(
                    name: "FK_IntelligenceJobLeases_IntelligenceJobs_JobId",
                    column: x => x.JobId,
                    principalTable: "IntelligenceJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_IntelligenceJobLeases_LeaseUntil",
            table: "IntelligenceJobLeases",
            column: "LeaseUntil");

        migrationBuilder.CreateIndex(
            name: "IX_IntelligenceWatermarks_Key",
            table: "IntelligenceWatermarks",
            column: "Key",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "IntelligenceJobLeases");
        migrationBuilder.DropTable(name: "IntelligenceWatermarks");
    }
}
