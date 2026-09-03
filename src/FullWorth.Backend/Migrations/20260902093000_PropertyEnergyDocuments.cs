using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260902093000_PropertyEnergyDocuments")]
public sealed class PropertyEnergyDocuments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE "AssetDocuments" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "Category" varchar(32) NOT NULL,
    "OriginalFileName" varchar(500) NOT NULL,
    "MediaType" varchar(100) NOT NULL,
    "StoragePath" varchar(1000) NOT NULL,
    "Sha256" varchar(64) NOT NULL,
    "SizeBytes" bigint NOT NULL,
    "Notes" varchar(2000) NULL,
    "CreatedByUserId" uuid NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_AssetDocuments" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AssetDocuments_FullWorthSpaces" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AssetDocuments_Assets" FOREIGN KEY ("AssetId") REFERENCES "Assets"("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AssetDocuments_Users" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users"("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_AssetDocuments_Category" CHECK ("Category" IN ('deed','purchase_contract','energy_certificate','appraisal','insurance','loan','invoice','photo','other')),
    CONSTRAINT "CK_AssetDocuments_Size" CHECK ("SizeBytes" > 0),
    CONSTRAINT "CK_AssetDocuments_Sha" CHECK (length("Sha256") = 64)
);
CREATE INDEX "IX_AssetDocuments_AssetId_CreatedAt" ON "AssetDocuments"("AssetId", "CreatedAt" DESC);
CREATE INDEX "IX_AssetDocuments_FullWorthSpaceId" ON "AssetDocuments"("FullWorthSpaceId");
CREATE UNIQUE INDEX "UX_AssetDocuments_Space_Sha" ON "AssetDocuments"("FullWorthSpaceId", "Sha256");

CREATE TABLE "PropertyEnergyCertificates" (
    "Id" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "CertificateType" varchar(16) NOT NULL,
    "EnergyClass" varchar(3) NULL,
    "EnergyValueKwhSqmYear" numeric(12,3) NULL,
    "PrimaryEnergySource" varchar(120) NULL,
    "IssuedAt" date NULL,
    "ValidUntil" date NULL,
    "BuildingYearOnCertificate" integer NULL,
    "DocumentId" uuid NULL,
    "IsCurrent" boolean NOT NULL DEFAULT true,
    "Notes" varchar(2000) NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_PropertyEnergyCertificates" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_PropertyEnergyCertificates_RealEstate" FOREIGN KEY ("AssetId") REFERENCES "RealEstateAssetDetails"("AssetId") ON DELETE CASCADE,
    CONSTRAINT "FK_PropertyEnergyCertificates_Document" FOREIGN KEY ("DocumentId") REFERENCES "AssetDocuments"("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_PropertyEnergyCertificates_Type" CHECK ("CertificateType" IN ('demand','consumption')),
    CONSTRAINT "CK_PropertyEnergyCertificates_Class" CHECK ("EnergyClass" IS NULL OR "EnergyClass" IN ('A+','A','B','C','D','E','F','G','H')),
    CONSTRAINT "CK_PropertyEnergyCertificates_Value" CHECK ("EnergyValueKwhSqmYear" IS NULL OR "EnergyValueKwhSqmYear" >= 0),
    CONSTRAINT "CK_PropertyEnergyCertificates_Dates" CHECK ("ValidUntil" IS NULL OR "IssuedAt" IS NULL OR "ValidUntil" >= "IssuedAt"),
    CONSTRAINT "CK_PropertyEnergyCertificates_Year" CHECK ("BuildingYearOnCertificate" IS NULL OR "BuildingYearOnCertificate" BETWEEN 1000 AND 3000)
);
CREATE INDEX "IX_PropertyEnergyCertificates_AssetId" ON "PropertyEnergyCertificates"("AssetId", "CreatedAt" DESC);
CREATE UNIQUE INDEX "UX_PropertyEnergyCertificates_Current" ON "PropertyEnergyCertificates"("AssetId") WHERE "IsCurrent" = true;

CREATE OR REPLACE FUNCTION fullworth_validate_energy_document()
RETURNS trigger AS $$
DECLARE doc_asset uuid;
BEGIN
    IF NEW."DocumentId" IS NOT NULL THEN
        SELECT "AssetId" INTO doc_asset FROM "AssetDocuments" WHERE "Id"=NEW."DocumentId";
        IF doc_asset IS NULL OR doc_asset <> NEW."AssetId" THEN
            RAISE EXCEPTION 'Energy certificate document must belong to the same asset';
        END IF;
    END IF;
    NEW."UpdatedAt" := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_PropertyEnergyCertificates_Validate" BEFORE INSERT OR UPDATE ON "PropertyEnergyCertificates"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_energy_document();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_PropertyEnergyCertificates_Validate" ON "PropertyEnergyCertificates";
DROP FUNCTION IF EXISTS fullworth_validate_energy_document();
DROP TABLE IF EXISTS "PropertyEnergyCertificates";
DROP TABLE IF EXISTS "AssetDocuments";
""");
    }
}
