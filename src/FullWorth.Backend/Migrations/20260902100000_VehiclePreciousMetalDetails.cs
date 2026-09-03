using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260902100000_VehiclePreciousMetalDetails")]
public sealed class VehiclePreciousMetalDetails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE "VehicleAssetDetails" (
    "AssetId" uuid PRIMARY KEY REFERENCES "Assets"("Id") ON DELETE CASCADE,
    "VehicleType" varchar(20) NOT NULL,
    "Manufacturer" varchar(120) NULL,
    "Model" varchar(120) NULL,
    "Variant" varchar(120) NULL,
    "VIN" varchar(80) NULL,
    "LicensePlate" varchar(40) NULL,
    "FirstRegistrationDate" date NULL,
    "ModelYear" integer NULL,
    "MileageKm" integer NULL,
    "Powertrain" varchar(20) NULL,
    "PowerKw" numeric(12,3) NULL,
    "PurchaseDate" date NULL,
    "PurchasePrice" numeric(20,8) NULL,
    "PurchaseCurrency" varchar(3) NULL,
    "Condition" varchar(32) NULL,
    "AnnualMileageEstimate" integer NULL,
    "Notes" varchar(2000) NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_VehicleAssetDetails_Type" CHECK ("VehicleType" IN ('car','motorcycle','camper','boat','other')),
    CONSTRAINT "CK_VehicleAssetDetails_Powertrain" CHECK ("Powertrain" IS NULL OR "Powertrain" IN ('petrol','diesel','hybrid','phev','electric','other')),
    CONSTRAINT "CK_VehicleAssetDetails_ModelYear" CHECK ("ModelYear" IS NULL OR ("ModelYear" >= 1886 AND "ModelYear" <= 2200)),
    CONSTRAINT "CK_VehicleAssetDetails_Numbers" CHECK (
        ("MileageKm" IS NULL OR "MileageKm" >= 0) AND
        ("PowerKw" IS NULL OR "PowerKw" >= 0) AND
        ("PurchasePrice" IS NULL OR "PurchasePrice" >= 0) AND
        ("AnnualMileageEstimate" IS NULL OR "AnnualMileageEstimate" >= 0)),
    CONSTRAINT "CK_VehicleAssetDetails_Currency" CHECK ("PurchaseCurrency" IS NULL OR "PurchaseCurrency" ~ '^[A-Z]{3}$')
);

CREATE TABLE "PreciousMetalAssetDetails" (
    "AssetId" uuid PRIMARY KEY REFERENCES "Assets"("Id") ON DELETE CASCADE,
    "MetalType" varchar(20) NOT NULL,
    "Form" varchar(20) NOT NULL,
    "Quantity" numeric(20,8) NOT NULL DEFAULT 1,
    "GrossWeightGrams" numeric(20,8) NULL,
    "Purity" numeric(12,8) NULL,
    "StorageLabel" varchar(200) NULL,
    "PurchaseDate" date NULL,
    "PurchasePrice" numeric(20,8) NULL,
    "PurchaseCurrency" varchar(3) NULL,
    "Notes" varchar(2000) NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_PreciousMetalAssetDetails_Type" CHECK ("MetalType" IN ('gold','silver','platinum','palladium','other')),
    CONSTRAINT "CK_PreciousMetalAssetDetails_Form" CHECK ("Form" IN ('bar','coin','jewelry','other')),
    CONSTRAINT "CK_PreciousMetalAssetDetails_Numbers" CHECK (
        "Quantity" > 0 AND
        ("GrossWeightGrams" IS NULL OR "GrossWeightGrams" >= 0) AND
        ("Purity" IS NULL OR ("Purity" >= 0 AND "Purity" <= 1)) AND
        ("PurchasePrice" IS NULL OR "PurchasePrice" >= 0)),
    CONSTRAINT "CK_PreciousMetalAssetDetails_Currency" CHECK ("PurchaseCurrency" IS NULL OR "PurchaseCurrency" ~ '^[A-Z]{3}$')
);

CREATE OR REPLACE FUNCTION fullworth_validate_specialized_asset_kind() RETURNS trigger AS $$
DECLARE asset_kind text;
BEGIN
    SELECT "Kind" INTO asset_kind FROM "Assets" WHERE "Id" = NEW."AssetId";
    IF TG_TABLE_NAME = 'VehicleAssetDetails' AND asset_kind <> 'vehicle' THEN
        RAISE EXCEPTION 'Vehicle details require a vehicle asset';
    END IF;
    IF TG_TABLE_NAME = 'PreciousMetalAssetDetails' AND asset_kind <> 'precious_metal' THEN
        RAISE EXCEPTION 'Precious-metal details require a precious-metal asset';
    END IF;
    NEW."UpdatedAt" = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER "TR_VehicleAssetDetails_Kind"
BEFORE INSERT OR UPDATE ON "VehicleAssetDetails"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_specialized_asset_kind();

CREATE TRIGGER "TR_PreciousMetalAssetDetails_Kind"
BEFORE INSERT OR UPDATE ON "PreciousMetalAssetDetails"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_specialized_asset_kind();

CREATE OR REPLACE FUNCTION fullworth_protect_specialized_asset_kind() RETURNS trigger AS $$
BEGIN
    IF OLD."Kind" = NEW."Kind" THEN RETURN NEW; END IF;
    IF OLD."Kind" = 'vehicle' AND EXISTS (SELECT 1 FROM "VehicleAssetDetails" WHERE "AssetId" = OLD."Id") THEN
        RAISE EXCEPTION 'Remove vehicle details before changing asset kind';
    END IF;
    IF OLD."Kind" = 'precious_metal' AND EXISTS (SELECT 1 FROM "PreciousMetalAssetDetails" WHERE "AssetId" = OLD."Id") THEN
        RAISE EXCEPTION 'Remove precious-metal details before changing asset kind';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER "TR_Assets_ProtectSpecializedKind"
BEFORE UPDATE OF "Kind" ON "Assets"
FOR EACH ROW EXECUTE FUNCTION fullworth_protect_specialized_asset_kind();
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_Assets_ProtectSpecializedKind" ON "Assets";
DROP FUNCTION IF EXISTS fullworth_protect_specialized_asset_kind();
DROP TRIGGER IF EXISTS "TR_VehicleAssetDetails_Kind" ON "VehicleAssetDetails";
DROP TRIGGER IF EXISTS "TR_PreciousMetalAssetDetails_Kind" ON "PreciousMetalAssetDetails";
DROP FUNCTION IF EXISTS fullworth_validate_specialized_asset_kind();
DROP TABLE IF EXISTS "PreciousMetalAssetDetails";
DROP TABLE IF EXISTS "VehicleAssetDetails";
""");
}
