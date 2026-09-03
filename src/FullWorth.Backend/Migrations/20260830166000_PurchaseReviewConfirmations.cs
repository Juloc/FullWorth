using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830166000_PurchaseReviewConfirmations")]
public partial class PurchaseReviewConfirmations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "PurchaseReconciliationConfirmations" (
  "PurchaseId" uuid PRIMARY KEY REFERENCES "Purchases"("Id") ON DELETE CASCADE,
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE RESTRICT,
  "ItemDifference" numeric(20,8) NOT NULL,
  "TransactionDifference" numeric(20,8) NULL,
  "ConfirmedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_PurchaseReconciliationConfirmations_Space"
  ON "PurchaseReconciliationConfirmations"("FullWorthSpaceId","ConfirmedAt" DESC);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"PurchaseReconciliationConfirmations\";");
    }
}
