using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260906113000_ContentAddressedBrandPacks")]
public sealed class ContentAddressedBrandPacks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "BrandAssetBlobs" (
    "Id" uuid NOT NULL,
    "ContentSha256" character varying(64) NOT NULL,
    "MediaType" character varying(80) NOT NULL,
    "ByteLength" integer NOT NULL,
    "Content" bytea NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastUsedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_BrandAssetBlobs" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BrandAssetBlobs_ContentSha256"
    ON "BrandAssetBlobs" ("ContentSha256");

INSERT INTO "BrandAssetBlobs"
    ("Id", "ContentSha256", "MediaType", "ByteLength", "Content", "CreatedAt", "LastUsedAt")
SELECT
    gen_random_uuid(),
    lower("ContentSha256"),
    "MediaType",
    octet_length(decode("ContentBase64", 'base64')),
    decode("ContentBase64", 'base64'),
    NOW(),
    NOW()
FROM "OfficialBrandAssets"
WHERE COALESCE("ContentBase64", '') <> ''
ON CONFLICT ("ContentSha256") DO NOTHING;

ALTER TABLE "OfficialBrandAssets"
    ADD COLUMN IF NOT EXISTS "ByteLength" integer NOT NULL DEFAULT 0;

UPDATE "OfficialBrandAssets" a
SET "ByteLength" = b."ByteLength"
FROM "BrandAssetBlobs" b
WHERE lower(a."ContentSha256") = b."ContentSha256"
  AND a."ByteLength" = 0;

ALTER TABLE "OfficialBrandAssets"
    DROP COLUMN IF EXISTS "ContentBase64";

CREATE INDEX IF NOT EXISTS "IX_OfficialBrandAssets_ContentSha256"
    ON "OfficialBrandAssets" ("ContentSha256");

CREATE TABLE IF NOT EXISTS "CustomBrandPacks" (
    "Id" uuid NOT NULL,
    "Name" character varying(120) NOT NULL,
    "Version" character varying(80) NOT NULL,
    "Priority" integer NOT NULL,
    "Enabled" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_CustomBrandPacks" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CustomBrandPacks_Name"
    ON "CustomBrandPacks" ("Name");

CREATE TABLE IF NOT EXISTS "CustomBrandAssets" (
    "Id" uuid NOT NULL,
    "PackId" uuid NOT NULL,
    "BrandKey" character varying(120) NOT NULL,
    "CanonicalName" character varying(200) NOT NULL,
    "LogoKey" character varying(120) NOT NULL,
    "MediaType" character varying(80) NOT NULL,
    "ContentSha256" character varying(64) NOT NULL,
    "ByteLength" integer NOT NULL,
    "SourceName" character varying(200),
    "SourceUrl" character varying(1000),
    "LicenseNote" character varying(500),
    CONSTRAINT "PK_CustomBrandAssets" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CustomBrandAssets_PackId_BrandKey"
    ON "CustomBrandAssets" ("PackId", "BrandKey");
CREATE INDEX IF NOT EXISTS "IX_CustomBrandAssets_ContentSha256"
    ON "CustomBrandAssets" ("ContentSha256");

CREATE TABLE IF NOT EXISTS "CustomBrandAliases" (
    "Id" uuid NOT NULL,
    "PackId" uuid NOT NULL,
    "AliasKey" character varying(300) NOT NULL,
    "BrandKey" character varying(120) NOT NULL,
    "Country" character varying(8) NOT NULL,
    CONSTRAINT "PK_CustomBrandAliases" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CustomBrandAliases_PackId_AliasKey_Country"
    ON "CustomBrandAliases" ("PackId", "AliasKey", "Country");
CREATE INDEX IF NOT EXISTS "IX_CustomBrandAliases_PackId_BrandKey"
    ON "CustomBrandAliases" ("PackId", "BrandKey");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "OfficialBrandAssets"
    ADD COLUMN IF NOT EXISTS "ContentBase64" text NOT NULL DEFAULT '';

UPDATE "OfficialBrandAssets" a
SET "ContentBase64" = encode(b."Content", 'base64')
FROM "BrandAssetBlobs" b
WHERE lower(a."ContentSha256") = b."ContentSha256";

DROP TABLE IF EXISTS "CustomBrandAliases";
DROP TABLE IF EXISTS "CustomBrandAssets";
DROP TABLE IF EXISTS "CustomBrandPacks";

DROP INDEX IF EXISTS "IX_OfficialBrandAssets_ContentSha256";
ALTER TABLE "OfficialBrandAssets" DROP COLUMN IF EXISTS "ByteLength";

DROP TABLE IF EXISTS "BrandAssetBlobs";
""");
    }
}
