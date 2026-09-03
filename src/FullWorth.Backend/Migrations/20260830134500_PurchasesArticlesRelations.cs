using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Completes the relational constraints for the article/product model introduced by
/// PurchasesArticlesSystem. Kept separate so the data-bearing migration stays easy to review and the
/// final database schema exactly matches FullWorthDbContext.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830134500_PurchasesArticlesRelations")]
public sealed class PurchasesArticlesRelations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_TransactionAllocations_PurchaseItemId",
            table: "TransactionAllocations",
            column: "PurchaseItemId");

        migrationBuilder.CreateIndex(
            name: "IX_Purchases_MerchantId",
            table: "Purchases",
            column: "MerchantId");

        migrationBuilder.CreateIndex(
            name: "IX_Products_DefaultCategoryId",
            table: "Products",
            column: "DefaultCategoryId");

        migrationBuilder.CreateIndex(
            name: "IX_ProductAliases_MerchantId",
            table: "ProductAliases",
            column: "MerchantId");

        migrationBuilder.CreateIndex(
            name: "IX_PurchasePaymentLinks_FullWorthSpaceId",
            table: "PurchasePaymentLinks",
            column: "FullWorthSpaceId");

        migrationBuilder.AddForeignKey(
            name: "FK_TransactionAllocations_PurchaseItems_PurchaseItemId",
            table: "TransactionAllocations",
            column: "PurchaseItemId",
            principalTable: "PurchaseItems",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_Purchases_Merchants_MerchantId",
            table: "Purchases",
            column: "MerchantId",
            principalTable: "Merchants",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_Products_FullWorthSpaces_FullWorthSpaceId",
            table: "Products",
            column: "FullWorthSpaceId",
            principalTable: "FullWorthSpaces",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_Products_Categories_DefaultCategoryId",
            table: "Products",
            column: "DefaultCategoryId",
            principalTable: "Categories",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_ProductAliases_Merchants_MerchantId",
            table: "ProductAliases",
            column: "MerchantId",
            principalTable: "Merchants",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_PurchasePaymentLinks_FullWorthSpaces_FullWorthSpaceId",
            table: "PurchasePaymentLinks",
            column: "FullWorthSpaceId",
            principalTable: "FullWorthSpaces",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_FinanceTags_FullWorthSpaces_FullWorthSpaceId",
            table: "FinanceTags",
            column: "FullWorthSpaceId",
            principalTable: "FullWorthSpaces",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_TransactionAllocations_PurchaseItems_PurchaseItemId", table: "TransactionAllocations");
        migrationBuilder.DropForeignKey(name: "FK_Purchases_Merchants_MerchantId", table: "Purchases");
        migrationBuilder.DropForeignKey(name: "FK_Products_FullWorthSpaces_FullWorthSpaceId", table: "Products");
        migrationBuilder.DropForeignKey(name: "FK_Products_Categories_DefaultCategoryId", table: "Products");
        migrationBuilder.DropForeignKey(name: "FK_ProductAliases_Merchants_MerchantId", table: "ProductAliases");
        migrationBuilder.DropForeignKey(name: "FK_PurchasePaymentLinks_FullWorthSpaces_FullWorthSpaceId", table: "PurchasePaymentLinks");
        migrationBuilder.DropForeignKey(name: "FK_FinanceTags_FullWorthSpaces_FullWorthSpaceId", table: "FinanceTags");

        migrationBuilder.DropIndex(name: "IX_TransactionAllocations_PurchaseItemId", table: "TransactionAllocations");
        migrationBuilder.DropIndex(name: "IX_Purchases_MerchantId", table: "Purchases");
        migrationBuilder.DropIndex(name: "IX_Products_DefaultCategoryId", table: "Products");
        migrationBuilder.DropIndex(name: "IX_ProductAliases_MerchantId", table: "ProductAliases");
        migrationBuilder.DropIndex(name: "IX_PurchasePaymentLinks_FullWorthSpaceId", table: "PurchasePaymentLinks");
    }
}
