using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Die Ablage fuer selbst recherchierte Logos (#176).
///
/// Dieselbe Form wie die Pakete daneben, damit der Katalog nichts Neues lernen muss: Marke, Alias,
/// Inhalt ueber <c>BrandAssetBlobs</c>. Dazu die Versuchsliste - EIN Versuch je Haendlername, auch
/// wenn er nichts ergeben hat, sonst loeste ein frischer Import mit hunderten unbekannten Haendlern
/// jedes Mal hunderte Aufrufe aus.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260922240000_ResearchedBrandLogos")]
public sealed class ResearchedBrandLogos : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "ResearchedBrandAssets" (
    "Id" uuid NOT NULL,
    "BrandKey" character varying(120) NOT NULL,
    "CanonicalName" character varying(200) NOT NULL,
    "LogoKey" character varying(120) NOT NULL,
    "MediaType" character varying(80) NOT NULL,
    "ContentSha256" character varying(64) NOT NULL,
    "ByteLength" integer NOT NULL,
    "SourceUrl" character varying(1000) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ResearchedBrandAssets" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ResearchedBrandAssets_BrandKey"
    ON "ResearchedBrandAssets" ("BrandKey");
CREATE INDEX IF NOT EXISTS "IX_ResearchedBrandAssets_ContentSha256"
    ON "ResearchedBrandAssets" ("ContentSha256");

CREATE TABLE IF NOT EXISTS "ResearchedBrandAliases" (
    "Id" uuid NOT NULL,
    "AliasKey" character varying(300) NOT NULL,
    "BrandKey" character varying(120) NOT NULL,
    "Country" character varying(8) NOT NULL,
    CONSTRAINT "PK_ResearchedBrandAliases" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ResearchedBrandAliases_AliasKey_Country"
    ON "ResearchedBrandAliases" ("AliasKey", "Country");
CREATE INDEX IF NOT EXISTS "IX_ResearchedBrandAliases_BrandKey"
    ON "ResearchedBrandAliases" ("BrandKey");

CREATE TABLE IF NOT EXISTS "BrandLogoResearchAttempts" (
    "Id" uuid NOT NULL,
    "AliasHash" character varying(64) NOT NULL,
    "Outcome" character varying(40) NOT NULL,
    "Domain" character varying(253) NULL,
    "AttemptedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_BrandLogoResearchAttempts" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BrandLogoResearchAttempts_AliasHash"
    ON "BrandLogoResearchAttempts" ("AliasHash");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "BrandLogoResearchAttempts";
DROP TABLE IF EXISTS "ResearchedBrandAliases";
DROP TABLE IF EXISTS "ResearchedBrandAssets";
""");
    }
}
