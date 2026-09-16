using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Receipt import staging belongs completely to one FullWorth space. The original raw-SQL schema
/// nevertheless used RESTRICT for both space foreign keys, so deleting a personal space failed as
/// soon as it had ever imported a receipt. Make the ownership rule explicit in the database instead
/// of teaching every space-deletion path about two tables that are intentionally outside the EF model.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260916203000_ReceiptImportSpaceCascade")]
public sealed class ReceiptImportSpaceCascade : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "ReceiptImportItems"
  DROP CONSTRAINT IF EXISTS "ReceiptImportItems_FullWorthSpaceId_fkey";
ALTER TABLE "ReceiptImportItems"
  ADD CONSTRAINT "ReceiptImportItems_FullWorthSpaceId_fkey"
  FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE;

ALTER TABLE "ReceiptImportBatches"
  DROP CONSTRAINT IF EXISTS "ReceiptImportBatches_FullWorthSpaceId_fkey";
ALTER TABLE "ReceiptImportBatches"
  ADD CONSTRAINT "ReceiptImportBatches_FullWorthSpaceId_fkey"
  FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE;
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "ReceiptImportItems"
  DROP CONSTRAINT IF EXISTS "ReceiptImportItems_FullWorthSpaceId_fkey";
ALTER TABLE "ReceiptImportItems"
  ADD CONSTRAINT "ReceiptImportItems_FullWorthSpaceId_fkey"
  FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT;

ALTER TABLE "ReceiptImportBatches"
  DROP CONSTRAINT IF EXISTS "ReceiptImportBatches_FullWorthSpaceId_fkey";
ALTER TABLE "ReceiptImportBatches"
  ADD CONSTRAINT "ReceiptImportBatches_FullWorthSpaceId_fkey"
  FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT;
""");
}
