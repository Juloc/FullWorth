using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

public partial class AccountDeletionTombstone : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsTombstone",
            table: "Users",
            type: "boolean",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IsTombstone", table: "Users");
    }
}
