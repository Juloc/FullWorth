using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260901220000_RealEstateCore")]
public sealed class RealEstateCore : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE "RealEstateAssetDetails" (
    "AssetId" uuid NOT NULL,
    "PropertyType" varchar(32) NOT NULL DEFAULT 'apartment',
    "UsageType" varchar(24) NOT NULL DEFAULT 'owner_occupied',
    "CountryCode" varchar(2) NOT NULL DEFAULT 'DE',
    "PostalCode" varchar(20) NULL,
    "City" varchar(160) NULL,
    "Street" varchar(200) NULL,
    "HouseNumber" varchar(40) NULL,
    "AddressExtra" varchar(200) NULL,
    "UnitLabel" varchar(100) NULL,
    "Latitude" numeric(9,6) NULL,
    "Longitude" numeric(9,6) NULL,
    "YearBuilt" integer NULL,
    "LastMajorModernizationYear" integer NULL,
    "LivingAreaSqm" numeric(12,3) NULL,
    "UsableAreaSqm" numeric(12,3) NULL,
    "PlotAreaSqm" numeric(14,3) NULL,
    "Rooms" numeric(8,2) NULL,
    "Bedrooms" integer NULL,
    "Bathrooms" integer NULL,
    "Floor" integer NULL,
    "TotalFloors" integer NULL,
    "OwnershipSharePercent" numeric(8,4) NOT NULL DEFAULT 100,
    "ParkingSpaces" integer NULL,
    "GarageSpaces" integer NULL,
    "Condition" varchar(32) NULL,
    "ConstructionType" varchar(100) NULL,
    "HeatingType" varchar(100) NULL,
    "PrimaryEnergySource" varchar(100) NULL,
    "Elevator" boolean NULL,
    "BarrierFree" boolean NULL,
    "BalconyTerrace" boolean NULL,
    "Basement" boolean NULL,
    "Garden" boolean NULL,
    "PurchaseDate" date NULL,
    "PurchasePrice" numeric(20,8) NULL,
    "PurchaseCurrency" varchar(3) NULL,
    "AcquisitionCosts" numeric(20,8) NULL,
    "EquityAtPurchase" numeric(20,8) NULL,
    "Notes" varchar(4000) NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_RealEstateAssetDetails" PRIMARY KEY ("AssetId"),
    CONSTRAINT "FK_RealEstateAssetDetails_Assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES "Assets"("Id") ON DELETE CASCADE,
    CONSTRAINT "CK_RealEstateAssetDetails_PropertyType" CHECK ("PropertyType" IN ('apartment','detached_house','semi_detached','row_house','multi_family','land','commercial','mixed','other')),
    CONSTRAINT "CK_RealEstateAssetDetails_UsageType" CHECK ("UsageType" IN ('owner_occupied','rented','mixed','vacant')),
    CONSTRAINT "CK_RealEstateAssetDetails_Country" CHECK ("CountryCode" ~ '^[A-Z]{2}$'),
    CONSTRAINT "CK_RealEstateAssetDetails_Ownership" CHECK ("OwnershipSharePercent" > 0 AND "OwnershipSharePercent" <= 100),
    CONSTRAINT "CK_RealEstateAssetDetails_Latitude" CHECK ("Latitude" IS NULL OR ("Latitude" >= -90 AND "Latitude" <= 90)),
    CONSTRAINT "CK_RealEstateAssetDetails_Longitude" CHECK ("Longitude" IS NULL OR ("Longitude" >= -180 AND "Longitude" <= 180)),
    CONSTRAINT "CK_RealEstateAssetDetails_YearBuilt" CHECK ("YearBuilt" IS NULL OR ("YearBuilt" >= 1000 AND "YearBuilt" <= 3000)),
    CONSTRAINT "CK_RealEstateAssetDetails_ModernizationYear" CHECK ("LastMajorModernizationYear" IS NULL OR ("LastMajorModernizationYear" >= 1000 AND "LastMajorModernizationYear" <= 3000)),
    CONSTRAINT "CK_RealEstateAssetDetails_Areas" CHECK (("LivingAreaSqm" IS NULL OR "LivingAreaSqm" >= 0) AND ("UsableAreaSqm" IS NULL OR "UsableAreaSqm" >= 0) AND ("PlotAreaSqm" IS NULL OR "PlotAreaSqm" >= 0)),
    CONSTRAINT "CK_RealEstateAssetDetails_Rooms" CHECK ("Rooms" IS NULL OR "Rooms" >= 0),
    CONSTRAINT "CK_RealEstateAssetDetails_Counts" CHECK (("Bedrooms" IS NULL OR "Bedrooms" >= 0) AND ("Bathrooms" IS NULL OR "Bathrooms" >= 0) AND ("TotalFloors" IS NULL OR "TotalFloors" >= 0) AND ("ParkingSpaces" IS NULL OR "ParkingSpaces" >= 0) AND ("GarageSpaces" IS NULL OR "GarageSpaces" >= 0)),
    CONSTRAINT "CK_RealEstateAssetDetails_Condition" CHECK ("Condition" IS NULL OR "Condition" IN ('new','renovated','good','needs_renovation','major_renovation','unknown')),
    CONSTRAINT "CK_RealEstateAssetDetails_Purchase" CHECK (("PurchasePrice" IS NULL OR "PurchasePrice" >= 0) AND ("AcquisitionCosts" IS NULL OR "AcquisitionCosts" >= 0) AND ("EquityAtPurchase" IS NULL OR "EquityAtPurchase" >= 0)),
    CONSTRAINT "CK_RealEstateAssetDetails_PurchaseCurrency" CHECK ("PurchaseCurrency" IS NULL OR "PurchaseCurrency" ~ '^[A-Z]{3}$')
);

CREATE TABLE "RealEstateAcquisitionCosts" (
    "Id" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "Type" varchar(32) NOT NULL,
    "Amount" numeric(20,8) NOT NULL,
    "Currency" varchar(3) NOT NULL,
    "Date" date NULL,
    "Notes" varchar(2000) NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_RealEstateAcquisitionCosts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_RealEstateAcquisitionCosts_RealEstateAssetDetails_AssetId" FOREIGN KEY ("AssetId") REFERENCES "RealEstateAssetDetails"("AssetId") ON DELETE CASCADE,
    CONSTRAINT "CK_RealEstateAcquisitionCosts_Type" CHECK ("Type" IN ('property_price','transfer_tax','notary','land_registry','broker','renovation_at_purchase','financing_fee','other')),
    CONSTRAINT "CK_RealEstateAcquisitionCosts_Amount" CHECK ("Amount" >= 0),
    CONSTRAINT "CK_RealEstateAcquisitionCosts_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$')
);
CREATE INDEX "IX_RealEstateAcquisitionCosts_AssetId_Date" ON "RealEstateAcquisitionCosts"("AssetId", "Date");

CREATE TABLE "AssetDebtLinks" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "LoanId" uuid NULL,
    "LiabilityId" uuid NULL,
    "RelationType" varchar(32) NOT NULL,
    "AllocationPercent" numeric(8,4) NOT NULL DEFAULT 100,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_AssetDebtLinks" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AssetDebtLinks_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AssetDebtLinks_Assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES "Assets"("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AssetDebtLinks_Loans_LoanId" FOREIGN KEY ("LoanId") REFERENCES "Loans"("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AssetDebtLinks_Liabilities_LiabilityId" FOREIGN KEY ("LiabilityId") REFERENCES "Liabilities"("Id") ON DELETE CASCADE,
    CONSTRAINT "CK_AssetDebtLinks_ExactlyOneDebt" CHECK ((("LoanId" IS NOT NULL)::int + ("LiabilityId" IS NOT NULL)::int) = 1),
    CONSTRAINT "CK_AssetDebtLinks_RelationType" CHECK ("RelationType" IN ('mortgage','vehicle_finance','secured_loan','other')),
    CONSTRAINT "CK_AssetDebtLinks_Allocation" CHECK ("AllocationPercent" > 0 AND "AllocationPercent" <= 100)
);
CREATE INDEX "IX_AssetDebtLinks_AssetId" ON "AssetDebtLinks"("AssetId");
CREATE INDEX "IX_AssetDebtLinks_LoanId" ON "AssetDebtLinks"("LoanId") WHERE "LoanId" IS NOT NULL;
CREATE INDEX "IX_AssetDebtLinks_LiabilityId" ON "AssetDebtLinks"("LiabilityId") WHERE "LiabilityId" IS NOT NULL;
CREATE UNIQUE INDEX "UX_AssetDebtLinks_Asset_Loan" ON "AssetDebtLinks"("AssetId", "LoanId") WHERE "LoanId" IS NOT NULL;
CREATE UNIQUE INDEX "UX_AssetDebtLinks_Asset_Liability" ON "AssetDebtLinks"("AssetId", "LiabilityId") WHERE "LiabilityId" IS NOT NULL;

CREATE OR REPLACE FUNCTION fullworth_validate_real_estate_detail()
RETURNS trigger AS $$
DECLARE
    asset_kind text;
BEGIN
    SELECT "Kind" INTO asset_kind FROM "Assets" WHERE "Id" = NEW."AssetId";
    IF asset_kind IS DISTINCT FROM 'real_estate' THEN
        RAISE EXCEPTION 'Real-estate details require an Asset with kind real_estate';
    END IF;
    NEW."CountryCode" := upper(btrim(NEW."CountryCode"));
    IF NEW."PurchaseCurrency" IS NOT NULL THEN
        NEW."PurchaseCurrency" := upper(btrim(NEW."PurchaseCurrency"));
    END IF;
    NEW."UpdatedAt" := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_RealEstateAssetDetails_Validate"
BEFORE INSERT OR UPDATE ON "RealEstateAssetDetails"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_real_estate_detail();

CREATE OR REPLACE FUNCTION fullworth_protect_real_estate_asset_kind()
RETURNS trigger AS $$
BEGIN
    IF OLD."Kind" = 'real_estate' AND NEW."Kind" <> 'real_estate'
       AND EXISTS (SELECT 1 FROM "RealEstateAssetDetails" d WHERE d."AssetId" = OLD."Id") THEN
        RAISE EXCEPTION 'Cannot change asset kind while real-estate details exist';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_Assets_ProtectRealEstateKind"
BEFORE UPDATE OF "Kind" ON "Assets"
FOR EACH ROW EXECUTE FUNCTION fullworth_protect_real_estate_asset_kind();

CREATE OR REPLACE FUNCTION fullworth_validate_asset_debt_link()
RETURNS trigger AS $$
DECLARE
    asset_space uuid;
    debt_space uuid;
    allocated numeric(12,4);
BEGIN
    SELECT "FullWorthSpaceId" INTO asset_space FROM "Assets" WHERE "Id" = NEW."AssetId";
    IF asset_space IS NULL OR asset_space <> NEW."FullWorthSpaceId" THEN
        RAISE EXCEPTION 'Asset debt link must use the asset FullWorth Space';
    END IF;

    IF NEW."LoanId" IS NOT NULL THEN
        SELECT "FullWorthSpaceId" INTO debt_space FROM "Loans" WHERE "Id" = NEW."LoanId" FOR UPDATE;
        SELECT COALESCE(SUM("AllocationPercent"), 0) INTO allocated
        FROM "AssetDebtLinks"
        WHERE "LoanId" = NEW."LoanId" AND "Id" <> NEW."Id";
    ELSE
        SELECT "FullWorthSpaceId" INTO debt_space FROM "Liabilities" WHERE "Id" = NEW."LiabilityId" FOR UPDATE;
        SELECT COALESCE(SUM("AllocationPercent"), 0) INTO allocated
        FROM "AssetDebtLinks"
        WHERE "LiabilityId" = NEW."LiabilityId" AND "Id" <> NEW."Id";
    END IF;

    IF debt_space IS NULL OR debt_space <> NEW."FullWorthSpaceId" THEN
        RAISE EXCEPTION 'Linked debt must be in the same FullWorth Space as the asset';
    END IF;
    IF allocated + NEW."AllocationPercent" > 100.0000 THEN
        RAISE EXCEPTION 'Debt allocation across assets cannot exceed 100 percent';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_AssetDebtLinks_Validate"
BEFORE INSERT OR UPDATE ON "AssetDebtLinks"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_asset_debt_link();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_AssetDebtLinks_Validate" ON "AssetDebtLinks";
DROP FUNCTION IF EXISTS fullworth_validate_asset_debt_link();
DROP TRIGGER IF EXISTS "TR_Assets_ProtectRealEstateKind" ON "Assets";
DROP FUNCTION IF EXISTS fullworth_protect_real_estate_asset_kind();
DROP TRIGGER IF EXISTS "TR_RealEstateAssetDetails_Validate" ON "RealEstateAssetDetails";
DROP FUNCTION IF EXISTS fullworth_validate_real_estate_detail();
DROP TABLE IF EXISTS "AssetDebtLinks";
DROP TABLE IF EXISTS "RealEstateAcquisitionCosts";
DROP TABLE IF EXISTS "RealEstateAssetDetails";
""");
    }
}
