using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830193000_AddReceiptScanJobs")]
public partial class AddReceiptScanJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ReceiptScanJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                FileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Engine = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReceiptScanJobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_ReceiptScanJobs_FullWorthSpaces_FullWorthSpaceId",
                    column: x => x.FullWorthSpaceId,
                    principalTable: "FullWorthSpaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ReceiptScanJobs_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ReceiptScanJobs_Purchases_PurchaseId",
                    column: x => x.PurchaseId,
                    principalTable: "Purchases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReceiptScanJobs_PurchaseId",
            table: "ReceiptScanJobs",
            column: "PurchaseId",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ReceiptScanJobs_Status_CreatedAt",
            table: "ReceiptScanJobs",
            columns: new[] { "Status", "CreatedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_ReceiptScanJobs_UserId_FullWorthSpaceId_CreatedAt",
            table: "ReceiptScanJobs",
            columns: new[] { "UserId", "FullWorthSpaceId", "CreatedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_ReceiptScanJobs_FullWorthSpaceId",
            table: "ReceiptScanJobs",
            column: "FullWorthSpaceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ReceiptScanJobs");
    }
}
