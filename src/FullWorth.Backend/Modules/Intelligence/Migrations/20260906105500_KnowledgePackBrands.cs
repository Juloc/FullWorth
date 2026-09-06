using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260906105500_KnowledgePackBrands")]
public sealed class KnowledgePackBrands : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "OfficialBrandAssets" (
    "Id" uuid NOT NULL,
    "BrandKey" character varying(120) NOT NULL,
    "CanonicalName" character varying(200) NOT NULL,
    "LogoKey" character varying(120) NOT NULL,
    "MediaType" character varying(80) NOT NULL,
    "ContentBase64" text NOT NULL,
    "ContentSha256" character varying(64) NOT NULL,
    "SourceName" character varying(200) NULL,
    "SourceUrl" character varying(1000) NULL,
    "LicenseNote" character varying(500) NULL,
    CONSTRAINT "PK_OfficialBrandAssets" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialBrandAssets_BrandKey"
    ON "OfficialBrandAssets" ("BrandKey");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialBrandAssets_LogoKey"
    ON "OfficialBrandAssets" ("LogoKey");

CREATE TABLE IF NOT EXISTS "OfficialBrandAliases" (
    "Id" uuid NOT NULL,
    "AliasKey" character varying(300) NOT NULL,
    "BrandKey" character varying(120) NOT NULL,
    "Country" character varying(8) NOT NULL,
    CONSTRAINT "PK_OfficialBrandAliases" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialBrandAliases_AliasKey_Country"
    ON "OfficialBrandAliases" ("AliasKey", "Country");
CREATE INDEX IF NOT EXISTS "IX_OfficialBrandAliases_BrandKey"
    ON "OfficialBrandAliases" ("BrandKey");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "OfficialBrandAliases";
DROP TABLE IF EXISTS "OfficialBrandAssets";
""");
    }
}
