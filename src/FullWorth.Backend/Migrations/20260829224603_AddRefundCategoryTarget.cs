using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundCategoryTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RefundCategoryId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_RefundCategoryId",
                table: "Transactions",
                column: "RefundCategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Categories_RefundCategoryId",
                table: "Transactions",
                column: "RefundCategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Categories_RefundCategoryId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_RefundCategoryId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "RefundCategoryId",
                table: "Transactions");
        }
    }
}
