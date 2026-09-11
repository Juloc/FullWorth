using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// A fund position can finally name the document it was read from.
///
/// Every other row a document commit writes names its document — the snapshot, the contribution, the
/// cost — and <c>BavInvestmentAllocations</c> was the one place the rule did not hold, so a position
/// committed from a Standmitteilung could only say <c>Source = 'document'</c> without saying which one.
/// That gap was found and written down while step 2 was being merged (docs/PENSION.md), and closing it
/// needed a column, which is this.
///
/// It matters more here than for the other three. A statement lists the fund split for one point in
/// time; two statements for neighbouring dates produce positions that are otherwise indistinguishable,
/// so without the document id there is no way to tell which reading a position came from — or to undo
/// one document's positions without touching the other's.
///
/// <c>ON DELETE SET NULL</c>, matching <c>BavCosts</c>: deleting a document must not delete the values a
/// person reviewed and accepted. They keep <c>Source = 'document'</c> and lose only the pointer.
///
/// Additive. No row is touched; every position that predates this keeps a NULL document, which is the
/// honest state for one that was typed in by hand or committed before the column existed.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260911120000_PensionAllocationDocument")]
public sealed class PensionAllocationDocument : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "BavInvestmentAllocations"
  ADD COLUMN IF NOT EXISTS "BavDocumentId" uuid NULL;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'FK_BavInvestmentAllocations_BavDocuments_BavDocumentId'
  ) THEN
    ALTER TABLE "BavInvestmentAllocations"
      ADD CONSTRAINT "FK_BavInvestmentAllocations_BavDocuments_BavDocumentId"
      FOREIGN KEY ("BavDocumentId") REFERENCES "BavDocuments" ("Id") ON DELETE SET NULL;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_BavInvestmentAllocations_BavDocumentId"
  ON "BavInvestmentAllocations" ("BavDocumentId");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_BavInvestmentAllocations_BavDocumentId";
ALTER TABLE "BavInvestmentAllocations"
  DROP CONSTRAINT IF EXISTS "FK_BavInvestmentAllocations_BavDocuments_BavDocumentId";
ALTER TABLE "BavInvestmentAllocations"
  DROP COLUMN IF EXISTS "BavDocumentId";
""");
    }
}
