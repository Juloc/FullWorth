using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260831123500_AddPurchaseReconciliationFingerprint")]
public partial class AddPurchaseReconciliationFingerprint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "StateFingerprint",
            table: "PurchaseReconciliationConfirmations",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "StateFingerprint",
            table: "PurchaseReconciliationConfirmations");
    }
}
