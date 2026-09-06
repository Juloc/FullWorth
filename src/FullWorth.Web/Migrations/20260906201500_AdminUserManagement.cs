using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Web.Migrations;

public partial class AdminUserManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsAdmin",
            schema: "auth",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "AdminAuditEvents",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ActorAuthUserId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetAuthUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AdminAuditEvents", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_AdminAuditEvents_OccurredAt",
            schema: "auth",
            table: "AdminAuditEvents",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_AdminAuditEvents_TargetAuthUserId",
            schema: "auth",
            table: "AdminAuditEvents",
            column: "TargetAuthUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AdminAuditEvents", schema: "auth");
        migrationBuilder.DropColumn(name: "IsAdmin", schema: "auth", table: "AspNetUsers");
    }
}
