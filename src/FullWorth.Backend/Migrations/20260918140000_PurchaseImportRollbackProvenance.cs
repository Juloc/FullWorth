using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// A receipt-batch or Amazon-sync import had no way back (#141): the only undo for "imported the wrong
/// file" or "synced against the wrong Amazon account" was deleting the resulting purchases by hand, one
/// at a time. This mirrors the transaction-import rollback (#109/#175) for purchases.
///
/// "PurchaseImportLinks" is the audit trail of what one import run created - deliberately not the same
/// thing as "ReceiptImportItems.PurchaseId", which is workflow bookkeeping (which item currently points
/// at which purchase) and may be reassigned. "ImportSource" separates the two kinds of run this table
/// serves ('receipt' | 'amazon'); the unique constraint on "PurchaseId" alone is what lets a rollback
/// scoped to one batch/run never touch a purchase another run is responsible for.
///
/// "ReceiptImportBatches" gets the same "RolledBackAt" a rolled-back batch needs to refuse a second
/// rollback, matching "PausedAt" (#134) as the pattern for a fact bolted onto an existing raw-SQL table.
///
/// "AmazonSyncRuns" is new: today a sync has no queryable identity at all, so there was nothing to scope
/// a rollback to and no history to show. It also carries "PurchasesCreated" - not "OrdersImported" -
/// because only the purchases THIS run actually created (not merely updated) are ever eligible for
/// rollback; see AmazonOrderSyncService for where that distinction is made.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260918140000_PurchaseImportRollbackProvenance")]
public sealed class PurchaseImportRollbackProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "PurchaseImportLinks" (
  "ImportBatchId" uuid NOT NULL,
  "ImportSource" character varying(16) NOT NULL,
  "PurchaseId" uuid NOT NULL,
  "CreatedAt" timestamp with time zone NOT NULL,
  CONSTRAINT "PK_PurchaseImportLinks" PRIMARY KEY ("ImportBatchId","PurchaseId"),
  CONSTRAINT "UQ_PurchaseImportLinks_PurchaseId" UNIQUE ("PurchaseId"),
  CONSTRAINT "FK_PurchaseImportLinks_Purchases_PurchaseId"
    FOREIGN KEY ("PurchaseId") REFERENCES "Purchases" ("Id") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS "IX_PurchaseImportLinks_PurchaseId" ON "PurchaseImportLinks" ("PurchaseId");

ALTER TABLE "ReceiptImportBatches"
  ADD COLUMN IF NOT EXISTS "RolledBackAt" timestamp with time zone NULL;

CREATE TABLE IF NOT EXISTS "AmazonSyncRuns" (
  "Id" uuid NOT NULL,
  "FullWorthSpaceId" uuid NOT NULL,
  "UserId" uuid NOT NULL,
  "StartedAt" timestamp with time zone NOT NULL,
  "CompletedAt" timestamp with time zone NULL,
  "Status" character varying(24) NOT NULL,
  "OrdersRead" integer NOT NULL DEFAULT 0,
  "PurchasesCreated" integer NOT NULL DEFAULT 0,
  "RolledBackAt" timestamp with time zone NULL,
  CONSTRAINT "PK_AmazonSyncRuns" PRIMARY KEY ("Id"),
  CONSTRAINT "FK_AmazonSyncRuns_FullWorthSpaces_FullWorthSpaceId"
    FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE,
  CONSTRAINT "FK_AmazonSyncRuns_Users_UserId"
    FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS "IX_AmazonSyncRuns_Space_StartedAt" ON "AmazonSyncRuns" ("FullWorthSpaceId","StartedAt" DESC);
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DROP TABLE IF EXISTS "AmazonSyncRuns";

ALTER TABLE "ReceiptImportBatches"
  DROP COLUMN IF EXISTS "RolledBackAt";

DROP TABLE IF EXISTS "PurchaseImportLinks";
""");
}
