using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Amazon integration was developed in parallel with the general article system and temporarily
/// introduced PurchaseTransactionLinks for the same many-to-many purchase/payment relationship.
/// FullWorth has one canonical model: PurchasePaymentLinks. This migration preserves any already
/// synced Amazon allocations, makes the pair unique, then removes the duplicate table.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830151000_UnifyPurchasePaymentLinks")]
public sealed class UnifyPurchasePaymentLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The feature branch has never intentionally written duplicate pairs, but normalize defensively
        // before turning the existing non-unique index into a unique contract.
        migrationBuilder.Sql("""
            DELETE FROM "PurchasePaymentLinks" older
            USING "PurchasePaymentLinks" newer
            WHERE older."PurchaseId" = newer."PurchaseId"
              AND older."TransactionId" = newer."TransactionId"
              AND (older."UpdatedAt", older."Id") < (newer."UpdatedAt", newer."Id");
            """);

        migrationBuilder.DropIndex(
            name: "IX_PurchasePaymentLinks_PurchaseId_TransactionId",
            table: "PurchasePaymentLinks");

        migrationBuilder.CreateIndex(
            name: "IX_PurchasePaymentLinks_PurchaseId_TransactionId",
            table: "PurchasePaymentLinks",
            columns: new[] { "PurchaseId", "TransactionId" },
            unique: true);

        // Import links written by the already-released Amazon migration. md5(text)::uuid is a
        // deterministic UUID requiring no PostgreSQL extension. Existing canonical rows win on Id;
        // the Amazon allocation values win on the pair because they may reflect a later manual split.
        migrationBuilder.Sql("""
            INSERT INTO "PurchasePaymentLinks"
                ("Id", "FullWorthSpaceId", "PurchaseId", "TransactionId", "Amount", "Currency",
                 "LinkSource", "Confidence", "CreatedByUserId", "CreatedAt", "UpdatedAt")
            SELECT
                md5(l."PurchaseId"::text || ':' || l."TransactionId"::text)::uuid,
                p."FullWorthSpaceId",
                l."PurchaseId",
                l."TransactionId",
                l."AllocatedAmount",
                p."Currency",
                l."Source",
                l."MatchConfidence",
                p."CreatedByUserId",
                l."CreatedAt",
                GREATEST(l."CreatedAt", p."UpdatedAt")
            FROM "PurchaseTransactionLinks" l
            JOIN "Purchases" p ON p."Id" = l."PurchaseId"
            ON CONFLICT ("PurchaseId", "TransactionId") DO UPDATE SET
                "Amount" = EXCLUDED."Amount",
                "Currency" = EXCLUDED."Currency",
                "LinkSource" = EXCLUDED."LinkSource",
                "Confidence" = COALESCE(EXCLUDED."Confidence", "PurchasePaymentLinks"."Confidence"),
                "UpdatedAt" = GREATEST("PurchasePaymentLinks"."UpdatedAt", EXCLUDED."UpdatedAt");
            """);

        migrationBuilder.DropTable(name: "PurchaseTransactionLinks");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
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

        migrationBuilder.CreateIndex(
            name: "IX_PurchaseTransactionLinks_TransactionId",
            table: "PurchaseTransactionLinks",
            column: "TransactionId");

        migrationBuilder.Sql("""
            INSERT INTO "PurchaseTransactionLinks"
                ("PurchaseId", "TransactionId", "AllocatedAmount", "MatchConfidence", "Source", "CreatedAt")
            SELECT "PurchaseId", "TransactionId", "Amount", "Confidence", "LinkSource", "CreatedAt"
            FROM "PurchasePaymentLinks"
            ON CONFLICT ("PurchaseId", "TransactionId") DO NOTHING;
            """);

        migrationBuilder.DropIndex(
            name: "IX_PurchasePaymentLinks_PurchaseId_TransactionId",
            table: "PurchasePaymentLinks");

        migrationBuilder.CreateIndex(
            name: "IX_PurchasePaymentLinks_PurchaseId_TransactionId",
            table: "PurchasePaymentLinks",
            columns: new[] { "PurchaseId", "TransactionId" });
    }
}
