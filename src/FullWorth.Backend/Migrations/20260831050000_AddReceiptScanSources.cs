using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260831050000_AddReceiptScanSources")]
public partial class AddReceiptScanSourcesMainCompatibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Compatibility marker only. The canonical, richer ReceiptScanSources schema is created by
        // 20260830221000_AddReceiptScanSources. Existing main databases already have this migration ID
        // recorded; fresh/feature databases must not create the parallel table a second time.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deliberately no-op: the canonical table belongs to the earlier Purchases/Articles migration.
    }
}
