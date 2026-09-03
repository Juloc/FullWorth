using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Final relational parity fixes discovered while comparing the manually authored purchases/articles
/// migrations with the EF runtime model. Keep these fixes additive so already-applied migrations remain
/// immutable and production databases can move forward safely.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830214000_PurchaseSchemaParity")]
public sealed class PurchaseSchemaParity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_ProductBarcodes_ProductId",
            table: "ProductBarcodes",
            column: "ProductId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ProductBarcodes_ProductId",
            table: "ProductBarcodes");
    }
}
