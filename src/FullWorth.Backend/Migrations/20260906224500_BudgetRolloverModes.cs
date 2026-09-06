using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260906224500_BudgetRolloverModes")]
public sealed class BudgetRolloverModes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "CarryOverOverspend",
            table: "Budgets",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Existing CarryOver=true meant the legacy full rollover mode. Preserve it during upgrade.
        migrationBuilder.Sql("""
UPDATE "Budgets"
SET "CarryOverOverspend" = "CarryOver";
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "CarryOverOverspend",
            table: "Budgets");
}
