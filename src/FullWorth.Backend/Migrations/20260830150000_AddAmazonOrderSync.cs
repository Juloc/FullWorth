using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830150000_AddAmazonOrderSync")]
public partial class AddAmazonOrderSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AmazonConnections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Marketplace = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                EncryptedStorageState = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSuccessfulSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AmazonConnections", x => x.Id);
                table.ForeignKey("FK_AmazonConnections_FullWorthSpaces_FullWorthSpaceId", x => x.FullWorthSpaceId, "FullWorthSpaces", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_AmazonConnections_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AmazonOrderMetadata",
            columns: table => new
            {
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                NonBankPaymentAmount = table.Column<decimal>(type: "numeric(20,8)", nullable: false, defaultValue: 0m),
                NonBankPaymentSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "amazon"),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AmazonOrderMetadata", x => x.PurchaseId);
                table.ForeignKey("FK_AmazonOrderMetadata_Purchases_PurchaseId", x => x.PurchaseId, "Purchases", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseTransactionLinks",
            columns: table => new
            {
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                AllocatedAmount = table.Column<decimal>(type: "numeric(20,8)", nullable: false),
                MatchConfidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseTransactionLinks", x => new { x.PurchaseId, x.TransactionId });
                table.ForeignKey("FK_PurchaseTransactionLinks_Purchases_PurchaseId", x => x.PurchaseId, "Purchases", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_PurchaseTransactionLinks_Transactions_TransactionId", x => x.TransactionId, "Transactions", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseRefunds",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalRefundId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                RefundDate = table.Column<DateOnly>(type: "date", nullable: true),
                Amount = table.Column<decimal>(type: "numeric(20,8)", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                MatchConfidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseRefunds", x => x.Id);
                table.ForeignKey("FK_PurchaseRefunds_Purchases_PurchaseId", x => x.PurchaseId, "Purchases", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_PurchaseRefunds_Transactions_TransactionId", x => x.TransactionId, "Transactions", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex("IX_AmazonConnections_FullWorthSpaceId_UserId_Marketplace", "AmazonConnections", new[] { "FullWorthSpaceId", "UserId", "Marketplace" }, unique: true);
        migrationBuilder.CreateIndex("IX_AmazonConnections_Status_LastSuccessfulSyncAt", "AmazonConnections", new[] { "Status", "LastSuccessfulSyncAt" });
        migrationBuilder.CreateIndex("IX_PurchaseTransactionLinks_TransactionId", "PurchaseTransactionLinks", "TransactionId");
        migrationBuilder.CreateIndex("IX_PurchaseRefunds_PurchaseId_ExternalRefundId", "PurchaseRefunds", new[] { "PurchaseId", "ExternalRefundId" }, unique: true);
        migrationBuilder.CreateIndex("IX_PurchaseRefunds_TransactionId", "PurchaseRefunds", "TransactionId", unique: true);

        // Preserve every existing primary purchase link. TransactionId on Purchases was never unique,
        // so a single bank charge may legitimately have represented several purchases already.
        migrationBuilder.Sql("""
            INSERT INTO "PurchaseTransactionLinks"
                ("PurchaseId", "TransactionId", "AllocatedAmount", "MatchConfidence", "Source", "CreatedAt")
            SELECT p."Id", p."TransactionId",
                   LEAST(ABS(t."Amount"), GREATEST(0, p."TotalAmount")),
                   p."MatchConfidence", 'legacy', p."UpdatedAt"
            FROM "Purchases" p
            JOIN "Transactions" t ON t."Id" = p."TransactionId"
            WHERE p."TransactionId" IS NOT NULL
            ON CONFLICT ("PurchaseId", "TransactionId") DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("AmazonOrderMetadata");
        migrationBuilder.DropTable("AmazonConnections");
        migrationBuilder.DropTable("PurchaseRefunds");
        migrationBuilder.DropTable("PurchaseTransactionLinks");
    }
}