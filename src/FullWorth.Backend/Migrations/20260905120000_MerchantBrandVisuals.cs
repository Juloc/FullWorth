using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// §4 merchant brand visuals: local brand metadata on the merchant registry. Adds BrandKey /
/// LogoAssetPath / AccentKey (the effective/override visuals) plus BrandOverridden (true when the stored
/// values are authoritative, false when the brand is auto-resolved from the curated local catalog).
/// Hand-written raw SQL with IF (NOT) EXISTS guards to match the repo's additive migration style; the
/// frozen model snapshot is extended in MerchantBrandSnapshot.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260905120000_MerchantBrandVisuals")]
public partial class MerchantBrandVisuals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Merchants" ADD COLUMN IF NOT EXISTS "BrandKey" character varying(80) NULL;
ALTER TABLE "Merchants" ADD COLUMN IF NOT EXISTS "LogoAssetPath" character varying(400) NULL;
ALTER TABLE "Merchants" ADD COLUMN IF NOT EXISTS "AccentKey" character varying(40) NULL;
ALTER TABLE "Merchants" ADD COLUMN IF NOT EXISTS "BrandOverridden" boolean NOT NULL DEFAULT false;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Merchants" DROP COLUMN IF EXISTS "BrandOverridden";
ALTER TABLE "Merchants" DROP COLUMN IF EXISTS "AccentKey";
ALTER TABLE "Merchants" DROP COLUMN IF EXISTS "LogoAssetPath";
ALTER TABLE "Merchants" DROP COLUMN IF EXISTS "BrandKey";
""");
    }
}
