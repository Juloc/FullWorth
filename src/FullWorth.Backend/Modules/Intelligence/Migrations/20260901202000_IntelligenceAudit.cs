using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260901202000_IntelligenceAudit")]
public sealed class IntelligenceAuditMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IntelligenceAuditEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                EntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IntelligenceAuditEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_IntelligenceAuditEvents_OccurredAt",
            table: "IntelligenceAuditEvents",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_IntelligenceAuditEvents_ActorUserId_OccurredAt",
            table: "IntelligenceAuditEvents",
            columns: new[] { "ActorUserId", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "IntelligenceAuditEvents");
    }
}
