using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20261105110000_ReceiptScanLane")]
public partial class ReceiptScanLane : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Lane says which worker takes a queued job: NULL = nobody has looked at it yet (the fast lane
        // reads digital PDFs locally in milliseconds), 'ai' = it needs the model, which only the slow,
        // strictly serial lane may call. Store-backed SQL like the rest of ReceiptScanJobs.
        migrationBuilder.Sql("""
            ALTER TABLE "ReceiptScanJobs"
              ADD COLUMN IF NOT EXISTS "Lane" character varying(8) NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ReceiptScanJobs" DROP COLUMN IF EXISTS "Lane";
            """);
    }
}
