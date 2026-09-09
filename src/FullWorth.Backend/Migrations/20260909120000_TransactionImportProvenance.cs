using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260909120000_TransactionImportProvenance")]
public partial class TransactionImportProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Depot imports have had exact provenance since 20260906104500 and can therefore be rolled
        // back. Transaction imports could not: the only trace of a commit was the candidate row's
        // DuplicateStatus and an ExternalKey prefix, which is not enough to know WHICH transactions a
        // job created. This adds the same link table, so undoing a wrong file stops being manual work.
        migrationBuilder.Sql("""
ALTER TABLE "ImportJobs" ADD COLUMN IF NOT EXISTS "RolledBackAt" timestamptz NULL;

CREATE TABLE IF NOT EXISTS "ImportTransactionLinks" (
  "ImportJobId" uuid NOT NULL REFERENCES "ImportJobs"("Id") ON DELETE CASCADE,
  "TransactionId" uuid NOT NULL REFERENCES "Transactions"("Id") ON DELETE CASCADE,
  "CreatedAt" timestamptz NOT NULL,
  PRIMARY KEY ("ImportJobId","TransactionId"),
  CONSTRAINT "UQ_ImportTransactionLinks_Transaction" UNIQUE ("TransactionId")
);

CREATE INDEX IF NOT EXISTS "IX_ImportTransactionLinks_Transaction"
  ON "ImportTransactionLinks"("TransactionId");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "ImportTransactionLinks";
ALTER TABLE "ImportJobs" DROP COLUMN IF EXISTS "RolledBackAt";
""");
    }
}
