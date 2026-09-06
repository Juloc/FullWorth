using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260906093000_KnowledgePacks")]
public sealed class KnowledgePacks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "KnowledgePackInstallations" (
    "Id" uuid NOT NULL,
    "ScopeKey" character varying(32) NOT NULL,
    "PackId" character varying(120) NOT NULL,
    "Version" character varying(80) NOT NULL,
    "SchemaVersion" character varying(40) NOT NULL,
    "Region" character varying(32) NOT NULL,
    "ContentSha256" character varying(80) NOT NULL,
    "SignatureAlgorithm" character varying(40) NOT NULL,
    "MerchantMappingCount" integer NOT NULL,
    "InstalledAt" timestamp with time zone NOT NULL,
    "LastCheckedAt" timestamp with time zone NOT NULL,
    "LastErrorCode" character varying(120) NULL,
    CONSTRAINT "PK_KnowledgePackInstallations" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_KnowledgePackInstallations_ScopeKey"
    ON "KnowledgePackInstallations" ("ScopeKey");

CREATE TABLE IF NOT EXISTS "KnowledgePackArchives" (
    "Id" uuid NOT NULL,
    "PackId" character varying(120) NOT NULL,
    "Version" character varying(80) NOT NULL,
    "SchemaVersion" character varying(40) NOT NULL,
    "Region" character varying(32) NOT NULL,
    "ContentSha256" character varying(80) NOT NULL,
    "SignatureAlgorithm" character varying(40) NOT NULL,
    "SignatureBase64" text NOT NULL,
    "PayloadBase64" text NOT NULL,
    "VerifiedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_KnowledgePackArchives" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_KnowledgePackArchives_PackId_Version"
    ON "KnowledgePackArchives" ("PackId", "Version");

CREATE TABLE IF NOT EXISTS "OfficialMerchantMappings" (
    "Id" uuid NOT NULL,
    "PackId" character varying(120) NOT NULL,
    "PackVersion" character varying(80) NOT NULL,
    "AliasKey" character varying(300) NOT NULL,
    "Direction" character varying(16) NOT NULL,
    "CanonicalMerchantKey" character varying(180) NOT NULL,
    "CanonicalName" character varying(240) NOT NULL,
    "CategoryKey" character varying(180) NULL,
    "Country" character varying(8) NULL,
    "Confidence" numeric(6,5) NOT NULL,
    "Domain" character varying(255) NULL,
    "LogoKey" character varying(180) NULL,
    CONSTRAINT "PK_OfficialMerchantMappings" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialMerchantMappings_AliasKey_Direction_Country"
    ON "OfficialMerchantMappings" ("AliasKey", "Direction", "Country");
CREATE INDEX IF NOT EXISTS "IX_OfficialMerchantMappings_CanonicalMerchantKey"
    ON "OfficialMerchantMappings" ("CanonicalMerchantKey");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "OfficialMerchantMappings";
DROP TABLE IF EXISTS "KnowledgePackArchives";
DROP TABLE IF EXISTS "KnowledgePackInstallations";
""");
    }
}
