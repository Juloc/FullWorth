using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Web.Migrations;

public partial class AccountDeletion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletionRequestedAt",
            schema: "auth",
            table: "AspNetUsers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletionScheduledFor",
            schema: "auth",
            table: "AspNetUsers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletionLeaseUntil",
            schema: "auth",
            table: "AspNetUsers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeletionLastError",
            schema: "auth",
            table: "AspNetUsers",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "DeletionScheduledForIndex",
            schema: "auth",
            table: "AspNetUsers",
            column: "DeletionScheduledFor");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "DeletionScheduledForIndex",
            schema: "auth",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(name: "DeletionRequestedAt", schema: "auth", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "DeletionScheduledFor", schema: "auth", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "DeletionLeaseUntil", schema: "auth", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "DeletionLastError", schema: "auth", table: "AspNetUsers");
    }
}
