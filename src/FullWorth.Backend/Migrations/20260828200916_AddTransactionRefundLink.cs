using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionRefundLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RefundOfTransactionId",
                table: "Transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_RefundOfTransactionId",
                table: "Transactions",
                column: "RefundOfTransactionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Transactions_RefundOfTransactionId",
                table: "Transactions",
                column: "RefundOfTransactionId",
                principalTable: "Transactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Transactions_RefundOfTransactionId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_RefundOfTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "RefundOfTransactionId",
                table: "Transactions");
        }
    }
}
