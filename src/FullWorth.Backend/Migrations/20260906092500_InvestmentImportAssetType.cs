using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260906092500_InvestmentImportAssetType")]
public partial class InvestmentImportAssetType : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "InvestmentImportCandidates"
  ADD COLUMN IF NOT EXISTS "AssetType" varchar(32) NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "InvestmentImportCandidates"
  DROP COLUMN IF EXISTS "AssetType";
""");
    }
}
