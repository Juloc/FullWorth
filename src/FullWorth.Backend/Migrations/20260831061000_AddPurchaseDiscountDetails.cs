using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260831061000_AddPurchaseDiscountDetails")]
public partial class AddPurchaseDiscountDetailsMainCompatibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Compatibility marker only. Canonical purchase/item discount columns and PurchaseDiscounts
        // are created by the earlier Purchases/Articles migrations. Existing main databases already
        // have this migration ID recorded; fresh/feature databases must not add the same schema twice.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deliberately no-op: canonical discount data belongs to the Purchases/Articles migrations.
    }
}
