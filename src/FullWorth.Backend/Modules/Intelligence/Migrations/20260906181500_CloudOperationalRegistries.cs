using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260906181500_CloudOperationalRegistries")]
public sealed class CloudOperationalRegistries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "OfficialContractProviders" (
    "Id" uuid NOT NULL,
    "ProviderKey" character varying(180) NOT NULL,
    "CanonicalName" character varying(240) NOT NULL,
    "Domain" character varying(255) NULL,
    "ProviderCategory" character varying(120) NULL,
    "Country" character varying(8) NOT NULL,
    "BrandKey" character varying(120) NULL,
    "Version" integer NOT NULL,
    CONSTRAINT "PK_OfficialContractProviders" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialContractProviders_ProviderKey"
    ON "OfficialContractProviders" ("ProviderKey");

CREATE TABLE IF NOT EXISTS "OfficialContractSignatures" (
    "Id" uuid NOT NULL,
    "ProviderKey" character varying(180) NOT NULL,
    "MerchantFingerprint" character varying(320) NOT NULL,
    "ExpectedRecurrence" character varying(80) NULL,
    "Confidence" numeric(6,5) NOT NULL,
    CONSTRAINT "PK_OfficialContractSignatures" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialContractSignatures_ProviderKey_MerchantFingerprint"
    ON "OfficialContractSignatures" ("ProviderKey", "MerchantFingerprint");
CREATE INDEX IF NOT EXISTS "IX_OfficialContractSignatures_MerchantFingerprint"
    ON "OfficialContractSignatures" ("MerchantFingerprint");

CREATE TABLE IF NOT EXISTS "OfficialProducts" (
    "Id" uuid NOT NULL,
    "ProductKey" character varying(180) NOT NULL,
    "CanonicalName" character varying(240) NOT NULL,
    "BrandKey" character varying(120) NULL,
    "CategoryKey" character varying(180) NULL,
    "PackageQuantity" character varying(80) NULL,
    "PackageUnit" character varying(40) NULL,
    "Country" character varying(8) NOT NULL,
    "Version" integer NOT NULL,
    CONSTRAINT "PK_OfficialProducts" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialProducts_ProductKey"
    ON "OfficialProducts" ("ProductKey");

CREATE TABLE IF NOT EXISTS "OfficialProductGtins" (
    "Id" uuid NOT NULL,
    "ProductKey" character varying(180) NOT NULL,
    "Gtin" character varying(20) NOT NULL,
    CONSTRAINT "PK_OfficialProductGtins" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialProductGtins_Gtin"
    ON "OfficialProductGtins" ("Gtin");
CREATE INDEX IF NOT EXISTS "IX_OfficialProductGtins_ProductKey"
    ON "OfficialProductGtins" ("ProductKey");

CREATE TABLE IF NOT EXISTS "OfficialProductAliases" (
    "Id" uuid NOT NULL,
    "ProductKey" character varying(180) NOT NULL,
    "AliasKey" character varying(320) NOT NULL,
    "MerchantContext" character varying(320) NULL,
    "Confidence" numeric(6,5) NOT NULL,
    CONSTRAINT "PK_OfficialProductAliases" PRIMARY KEY ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_OfficialProductAliases_AliasKey_MerchantContext"
    ON "OfficialProductAliases" ("AliasKey", "MerchantContext");
CREATE INDEX IF NOT EXISTS "IX_OfficialProductAliases_ProductKey"
    ON "OfficialProductAliases" ("ProductKey");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "OfficialProductAliases";
DROP TABLE IF EXISTS "OfficialProductGtins";
DROP TABLE IF EXISTS "OfficialProducts";
DROP TABLE IF EXISTS "OfficialContractSignatures";
DROP TABLE IF EXISTS "OfficialContractProviders";
""");
    }
}
