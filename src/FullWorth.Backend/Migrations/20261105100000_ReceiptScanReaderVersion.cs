using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20261105100000_ReceiptScanReaderVersion")]
public partial class ReceiptScanReaderVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ReceiptScanJobs is store-backed SQL, not an EF aggregate (see AddReceiptScanSources), so the
        // columns are added here without touching the model snapshot.
        // ReaderVersion says WHICH reader produced the result ("text/2", "tesseract/2", "codex/<model>"),
        // Reason why the job took that road ("digital_pdf", "scan", "digital_unverified", "ai_unavailable").
        // Together they let a later reader change re-run exactly the receipts an older reader read.
        migrationBuilder.Sql("""
            ALTER TABLE "ReceiptScanJobs"
              ADD COLUMN IF NOT EXISTS "ReaderVersion" character varying(64) NULL,
              ADD COLUMN IF NOT EXISTS "Reason" character varying(32) NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ReceiptScanJobs"
              DROP COLUMN IF EXISTS "ReaderVersion",
              DROP COLUMN IF EXISTS "Reason";
            """);
    }
}
