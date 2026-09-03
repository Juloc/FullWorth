using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260902190000_WealthReleaseIndexes")]
public sealed class WealthReleaseIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
-- Most wealth indexes are created with their owning feature migrations. These cover the remaining
-- release-gate query shapes without duplicating equivalent indexes.
CREATE INDEX IF NOT EXISTS "IX_PropertyImprovements_AssetId_CompletedDate"
    ON "PropertyImprovements" ("AssetId", "CompletedDate" DESC)
    WHERE "CompletedDate" IS NOT NULL;

CREATE INDEX IF NOT EXISTS "IX_AssetDebtLinks_FullWorthSpaceId_AssetId"
    ON "AssetDebtLinks" ("FullWorthSpaceId", "AssetId");

CREATE INDEX IF NOT EXISTS "IX_AssetCashflowEntries_FullWorthSpaceId_AssetId_Date"
    ON "AssetCashflowEntries" ("FullWorthSpaceId", "AssetId", "Date" DESC);

CREATE INDEX IF NOT EXISTS "IX_RentalLeases_FullWorthSpaceId_AssetId_Status"
    ON "RentalLeases" ("FullWorthSpaceId", "AssetId", "Status");

CREATE INDEX IF NOT EXISTS "IX_ReceivablePayments_FullWorthSpaceId_AssetId_Date"
    ON "ReceivablePayments" ("FullWorthSpaceId", "AssetId", "Date" DESC);
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_ReceivablePayments_FullWorthSpaceId_AssetId_Date";
DROP INDEX IF EXISTS "IX_RentalLeases_FullWorthSpaceId_AssetId_Status";
DROP INDEX IF EXISTS "IX_AssetCashflowEntries_FullWorthSpaceId_AssetId_Date";
DROP INDEX IF EXISTS "IX_AssetDebtLinks_FullWorthSpaceId_AssetId";
DROP INDEX IF EXISTS "IX_PropertyImprovements_AssetId_CompletedDate";
""");
}
