using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260831050000_PurchaseAllocationProvenance")]
public partial class PurchaseAllocationProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PurchaseAllocationLinks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TransactionAllocationId = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseDiscountId = table.Column<Guid>(type: "uuid", nullable: true),
                AllocationType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseAllocationLinks", x => x.Id);
                table.ForeignKey(
                    name: "FK_PurchaseAllocationLinks_TransactionAllocations_TransactionAllocationId",
                    column: x => x.TransactionAllocationId,
                    principalTable: "TransactionAllocations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PurchaseAllocationLinks_Purchases_PurchaseId",
                    column: x => x.PurchaseId,
                    principalTable: "Purchases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PurchaseAllocationLinks_PurchaseDiscounts_PurchaseDiscountId",
                    column: x => x.PurchaseDiscountId,
                    principalTable: "PurchaseDiscounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PurchaseAllocationLinks_TransactionAllocationId",
            table: "PurchaseAllocationLinks",
            column: "TransactionAllocationId",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_PurchaseAllocationLinks_PurchaseId",
            table: "PurchaseAllocationLinks",
            column: "PurchaseId");
        migrationBuilder.CreateIndex(
            name: "IX_PurchaseAllocationLinks_PurchaseDiscountId",
            table: "PurchaseAllocationLinks",
            column: "PurchaseDiscountId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "PurchaseAllocationLinks");
}
