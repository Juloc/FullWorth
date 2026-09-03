using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830134000_CategoryIntelligence")]
public partial class CategoryIntelligence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CategoryAppearances",
            columns: table => new
            {
                CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CategoryAppearances", x => x.CategoryId);
                table.ForeignKey(
                    name: "FK_CategoryAppearances_Categories_CategoryId",
                    column: x => x.CategoryId,
                    principalTable: "Categories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CategoryAppearances_FullWorthSpaces_FullWorthSpaceId",
                    column: x => x.FullWorthSpaceId,
                    principalTable: "FullWorthSpaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // FinanceTags is created by the earlier canonical 20260830133000_PurchasesArticlesSystem
        // migration (character varying(100), no Color/UpdatedAt). The parallel category-intelligence
        // feature branch temporarily defined its own richer FinanceTags shape here; on the integrated
        // timeline that table already exists, so this migration only adds the transaction-tagging
        // structures that reference it.
        migrationBuilder.CreateTable(
            name: "TransactionReviewStates",
            columns: table => new
            {
                TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                IsReviewed = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TransactionReviewStates", x => x.TransactionId);
                table.ForeignKey(
                    name: "FK_TransactionReviewStates_FullWorthSpaces_FullWorthSpaceId",
                    column: x => x.FullWorthSpaceId,
                    principalTable: "FullWorthSpaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_TransactionReviewStates_Transactions_TransactionId",
                    column: x => x.TransactionId,
                    principalTable: "Transactions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TransactionTags",
            columns: table => new
            {
                TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                TagId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TransactionTags", x => new { x.TransactionId, x.TagId });
                table.ForeignKey(
                    name: "FK_TransactionTags_FinanceTags_TagId",
                    column: x => x.TagId,
                    principalTable: "FinanceTags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_TransactionTags_Transactions_TransactionId",
                    column: x => x.TransactionId,
                    principalTable: "Transactions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CategoryAppearances_FullWorthSpaceId",
            table: "CategoryAppearances",
            column: "FullWorthSpaceId");

        migrationBuilder.CreateIndex(
            name: "IX_TransactionReviewStates_FullWorthSpaceId_IsReviewed",
            table: "TransactionReviewStates",
            columns: new[] { "FullWorthSpaceId", "IsReviewed" });

        migrationBuilder.CreateIndex(
            name: "IX_TransactionTags_TagId",
            table: "TransactionTags",
            column: "TagId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CategoryAppearances");
        migrationBuilder.DropTable(name: "TransactionReviewStates");
        migrationBuilder.DropTable(name: "TransactionTags");
    }
}
