using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260906100000_KnowledgePackOntology")]
public sealed class KnowledgePackOntology : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "OfficialOntologyEntities" (
    "Id" uuid NOT NULL,
    "EntityType" character varying(32) NOT NULL,
    "CanonicalKey" character varying(180) NOT NULL,
    "DisplayName" character varying(200) NOT NULL,
    "ParentCanonicalKey" character varying(180) NULL,
    "Status" character varying(24) NOT NULL,
    "Version" integer NOT NULL,
    CONSTRAINT "PK_OfficialOntologyEntities" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialOntologyEntities_EntityType_CanonicalKey"
    ON "OfficialOntologyEntities" ("EntityType", "CanonicalKey");

CREATE TABLE IF NOT EXISTS "OfficialOntologyAliases" (
    "Id" uuid NOT NULL,
    "EntityType" character varying(32) NOT NULL,
    "CanonicalKey" character varying(180) NOT NULL,
    "Alias" character varying(200) NOT NULL,
    "NormalizedAlias" character varying(200) NOT NULL,
    "Locale" character varying(20) NOT NULL,
    "Country" character varying(8) NOT NULL,
    "Confidence" numeric(6,5) NOT NULL,
    "DistinctInstances" integer NOT NULL,
    "Version" integer NOT NULL,
    CONSTRAINT "PK_OfficialOntologyAliases" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_OfficialOntologyAliases_EntityType_NormalizedAlias_Locale_Country"
    ON "OfficialOntologyAliases" ("EntityType", "NormalizedAlias", "Locale", "Country");
CREATE INDEX IF NOT EXISTS "IX_OfficialOntologyAliases_EntityType_CanonicalKey"
    ON "OfficialOntologyAliases" ("EntityType", "CanonicalKey");

CREATE TABLE IF NOT EXISTS "OfficialOntologyRedirects" (
    "Id" uuid NOT NULL,
    "EntityType" character varying(32) NOT NULL,
    "FromCanonicalKey" character varying(180) NOT NULL,
    "ToCanonicalKey" character varying(180) NOT NULL,
    "Version" integer NOT NULL,
    CONSTRAINT "PK_OfficialOntologyRedirects" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialOntologyRedirects_EntityType_FromCanonicalKey"
    ON "OfficialOntologyRedirects" ("EntityType", "FromCanonicalKey");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "OfficialOntologyRedirects";
DROP TABLE IF EXISTS "OfficialOntologyAliases";
DROP TABLE IF EXISTS "OfficialOntologyEntities";
""");
    }
}
