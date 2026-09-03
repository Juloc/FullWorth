using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260831044500_PurchaseDiscountModel")]
public partial class PurchaseDiscountModel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "RoundingAmount",
            table: "Purchases",
            type: "numeric(20,8)",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<decimal>(
            name: "OriginalUnitPrice",
            table: "PurchaseItems",
            type: "numeric(20,8)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DiscountLabel",
            table: "PurchaseItems",
            type: "character varying(250)",
            maxLength: 250,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "PurchaseDiscounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseItemId = table.Column<Guid>(type: "uuid", nullable: true),
                Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Label = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,8)", nullable: false),
                Percentage = table.Column<decimal>(type: "numeric(8,4)", nullable: true),
                CouponCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                RawText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseDiscounts", x => x.Id);
                table.CheckConstraint("CK_PurchaseDiscounts_Amount_NonNegative", "\"Amount\" >= 0");
                table.CheckConstraint("CK_PurchaseDiscounts_Percentage_Range", "\"Percentage\" IS NULL OR (\"Percentage\" >= 0 AND \"Percentage\" <= 100)");
                table.ForeignKey(
                    name: "FK_PurchaseDiscounts_Purchases_PurchaseId",
                    column: x => x.PurchaseId,
                    principalTable: "Purchases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PurchaseDiscounts_PurchaseItems_PurchaseItemId",
                    column: x => x.PurchaseItemId,
                    principalTable: "PurchaseItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PurchaseDiscounts_PurchaseId_CreatedAt",
            table: "PurchaseDiscounts",
            columns: new[] { "PurchaseId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_PurchaseDiscounts_PurchaseItemId",
            table: "PurchaseDiscounts",
            column: "PurchaseItemId");

        // Preserve any already-structured item discount amounts as item-linked canonical discounts.
        migrationBuilder.Sql("""
            INSERT INTO "PurchaseDiscounts"
                ("Id", "PurchaseId", "PurchaseItemId", "Type", "Label", "Amount", "Percentage",
                 "CouponCode", "RawText", "Source", "Confidence", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), i."PurchaseId", i."Id", 'price_reduction',
                   COALESCE(NULLIF(i."DiscountLabel", ''), 'Legacy item discount'),
                   i."DiscountAmount", NULL, NULL, NULL, 'migration', NULL, now(), now()
            FROM "PurchaseItems" i
            WHERE COALESCE(i."DiscountAmount", 0) > 0;
            """);

        // The purchase-level total may contain basket discounts in addition to item reductions. Only
        // materialize the residual so item discounts are never duplicated in the canonical table.
        migrationBuilder.Sql("""
            WITH item_discount AS (
                SELECT i."PurchaseId", COALESCE(SUM(i."DiscountAmount"), 0) AS amount
                FROM "PurchaseItems" i
                WHERE COALESCE(i."DiscountAmount", 0) > 0
                GROUP BY i."PurchaseId"
            )
            INSERT INTO "PurchaseDiscounts"
                ("Id", "PurchaseId", "PurchaseItemId", "Type", "Label", "Amount", "Percentage",
                 "CouponCode", "RawText", "Source", "Confidence", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), p."Id", NULL, 'other', 'Legacy receipt discount',
                   p."DiscountAmount" - COALESCE(d.amount, 0), NULL, NULL, NULL, 'migration', NULL, now(), now()
            FROM "Purchases" p
            LEFT JOIN item_discount d ON d."PurchaseId" = p."Id"
            WHERE COALESCE(p."DiscountAmount", 0) - COALESCE(d.amount, 0) > 0;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PurchaseDiscounts");
        migrationBuilder.DropColumn(name: "RoundingAmount", table: "Purchases");
        migrationBuilder.DropColumn(name: "OriginalUnitPrice", table: "PurchaseItems");
        migrationBuilder.DropColumn(name: "DiscountLabel", table: "PurchaseItems");
    }
}
