using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260901233000_RealEstateOperations")]
public sealed class RealEstateOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE "PropertyUnits" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "Name" varchar(160) NOT NULL,
    "UnitType" varchar(24) NOT NULL DEFAULT 'apartment',
    "AreaSqm" numeric(12,3) NULL,
    "Rooms" numeric(8,2) NULL,
    "OwnershipSharePercent" numeric(8,4) NULL,
    "IsOwnerOccupied" boolean NOT NULL DEFAULT false,
    "IsActive" boolean NOT NULL DEFAULT true,
    "Notes" varchar(2000) NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_PropertyUnits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_PropertyUnits_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_PropertyUnits_RealEstateAssetDetails_AssetId" FOREIGN KEY ("AssetId") REFERENCES "RealEstateAssetDetails"("AssetId") ON DELETE CASCADE,
    CONSTRAINT "CK_PropertyUnits_UnitType" CHECK ("UnitType" IN ('apartment','commercial','parking','storage','other')),
    CONSTRAINT "CK_PropertyUnits_Area" CHECK ("AreaSqm" IS NULL OR "AreaSqm" >= 0),
    CONSTRAINT "CK_PropertyUnits_Rooms" CHECK ("Rooms" IS NULL OR "Rooms" >= 0),
    CONSTRAINT "CK_PropertyUnits_Ownership" CHECK ("OwnershipSharePercent" IS NULL OR ("OwnershipSharePercent" > 0 AND "OwnershipSharePercent" <= 100))
);
CREATE INDEX "IX_PropertyUnits_AssetId_IsActive" ON "PropertyUnits"("AssetId", "IsActive");

CREATE TABLE "RentalLeases" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "PropertyUnitId" uuid NOT NULL,
    "TenantDisplayLabel" varchar(160) NULL,
    "StartDate" date NOT NULL,
    "EndDate" date NULL,
    "Status" varchar(16) NOT NULL DEFAULT 'active',
    "ColdRent" numeric(20,8) NOT NULL,
    "UtilitiesAdvance" numeric(20,8) NULL,
    "OtherRecurringCharges" numeric(20,8) NULL,
    "Currency" varchar(3) NOT NULL,
    "PaymentCycle" varchar(16) NOT NULL DEFAULT 'monthly',
    "DepositAmount" numeric(20,8) NULL,
    "DepositHeld" boolean NULL,
    "LastRentChangeDate" date NULL,
    "NextReviewDate" date NULL,
    "Notes" varchar(2000) NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_RentalLeases" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_RentalLeases_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_RentalLeases_RealEstateAssetDetails_AssetId" FOREIGN KEY ("AssetId") REFERENCES "RealEstateAssetDetails"("AssetId") ON DELETE CASCADE,
    CONSTRAINT "FK_RentalLeases_PropertyUnits_PropertyUnitId" FOREIGN KEY ("PropertyUnitId") REFERENCES "PropertyUnits"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_RentalLeases_Status" CHECK ("Status" IN ('planned','active','ended')),
    CONSTRAINT "CK_RentalLeases_Dates" CHECK ("EndDate" IS NULL OR "EndDate" >= "StartDate"),
    CONSTRAINT "CK_RentalLeases_Amounts" CHECK ("ColdRent" >= 0 AND ("UtilitiesAdvance" IS NULL OR "UtilitiesAdvance" >= 0) AND ("OtherRecurringCharges" IS NULL OR "OtherRecurringCharges" >= 0) AND ("DepositAmount" IS NULL OR "DepositAmount" >= 0)),
    CONSTRAINT "CK_RentalLeases_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_RentalLeases_Cycle" CHECK ("PaymentCycle" IN ('weekly','monthly','quarterly','yearly'))
);
CREATE INDEX "IX_RentalLeases_AssetId_Status" ON "RentalLeases"("AssetId", "Status");
CREATE INDEX "IX_RentalLeases_Unit_Dates" ON "RentalLeases"("PropertyUnitId", "StartDate", "EndDate");

CREATE TABLE "AssetCashflowEntries" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "TransactionId" uuid NULL,
    "Date" date NOT NULL,
    "Type" varchar(32) NOT NULL,
    "Amount" numeric(20,8) NOT NULL,
    "Direction" varchar(8) NOT NULL,
    "Currency" varchar(3) NOT NULL,
    "IsPlanned" boolean NOT NULL DEFAULT false,
    "Notes" varchar(2000) NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_AssetCashflowEntries" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AssetCashflowEntries_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AssetCashflowEntries_Assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES "Assets"("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AssetCashflowEntries_Transactions_TransactionId" FOREIGN KEY ("TransactionId") REFERENCES "Transactions"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_AssetCashflowEntries_Type" CHECK ("Type" IN ('rental_income','income','operating_expense','capex','debt_payment','tax','insurance','fee','distribution','other')),
    CONSTRAINT "CK_AssetCashflowEntries_Amount" CHECK ("Amount" > 0),
    CONSTRAINT "CK_AssetCashflowEntries_Direction" CHECK ("Direction" IN ('income','expense')),
    CONSTRAINT "CK_AssetCashflowEntries_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_AssetCashflowEntries_TransactionActual" CHECK ("TransactionId" IS NULL OR "IsPlanned" = false)
);
CREATE INDEX "IX_AssetCashflowEntries_Asset_Date" ON "AssetCashflowEntries"("AssetId", "Date");
CREATE INDEX "IX_AssetCashflowEntries_TransactionId" ON "AssetCashflowEntries"("TransactionId") WHERE "TransactionId" IS NOT NULL;
CREATE UNIQUE INDEX "UX_AssetCashflowEntries_Asset_Transaction_Type" ON "AssetCashflowEntries"("AssetId", "TransactionId", "Type") WHERE "TransactionId" IS NOT NULL;

CREATE TABLE "PropertyImprovements" (
    "Id" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "Title" varchar(200) NOT NULL,
    "Category" varchar(32) NOT NULL,
    "StartDate" date NULL,
    "CompletedDate" date NULL,
    "Cost" numeric(20,8) NULL,
    "Currency" varchar(3) NULL,
    "EstimatedValueAdded" numeric(20,8) NULL,
    "Description" varchar(4000) NULL,
    "DocumentId" uuid NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_PropertyImprovements" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_PropertyImprovements_RealEstateAssetDetails_AssetId" FOREIGN KEY ("AssetId") REFERENCES "RealEstateAssetDetails"("AssetId") ON DELETE CASCADE,
    CONSTRAINT "CK_PropertyImprovements_Category" CHECK ("Category" IN ('windows','roof','heating','insulation','electrical','plumbing','bathroom','kitchen','flooring','facade','solar','structural','other')),
    CONSTRAINT "CK_PropertyImprovements_Dates" CHECK ("CompletedDate" IS NULL OR "StartDate" IS NULL OR "CompletedDate" >= "StartDate"),
    CONSTRAINT "CK_PropertyImprovements_Cost" CHECK ("Cost" IS NULL OR "Cost" >= 0),
    CONSTRAINT "CK_PropertyImprovements_ValueAdded" CHECK ("EstimatedValueAdded" IS NULL OR "EstimatedValueAdded" >= 0),
    CONSTRAINT "CK_PropertyImprovements_Currency" CHECK ("Currency" IS NULL OR "Currency" ~ '^[A-Z]{3}$')
);
CREATE INDEX "IX_PropertyImprovements_AssetId_StartDate" ON "PropertyImprovements"("AssetId", "StartDate");

CREATE TABLE "PropertyImprovementCashflows" (
    "ImprovementId" uuid NOT NULL,
    "CashflowEntryId" uuid NOT NULL,
    CONSTRAINT "PK_PropertyImprovementCashflows" PRIMARY KEY ("ImprovementId", "CashflowEntryId"),
    CONSTRAINT "FK_PropertyImprovementCashflows_Improvements" FOREIGN KEY ("ImprovementId") REFERENCES "PropertyImprovements"("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_PropertyImprovementCashflows_Cashflows" FOREIGN KEY ("CashflowEntryId") REFERENCES "AssetCashflowEntries"("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX "UX_PropertyImprovementCashflows_Cashflow" ON "PropertyImprovementCashflows"("CashflowEntryId");

CREATE TABLE "AssetRecurringContractLinks" (
    "FullWorthSpaceId" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "RecurringContractId" uuid NOT NULL,
    "Role" varchar(32) NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_AssetRecurringContractLinks" PRIMARY KEY ("AssetId", "RecurringContractId"),
    CONSTRAINT "FK_AssetRecurringContractLinks_FullWorthSpaces" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AssetRecurringContractLinks_Assets" FOREIGN KEY ("AssetId") REFERENCES "Assets"("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AssetRecurringContractLinks_Contracts" FOREIGN KEY ("RecurringContractId") REFERENCES "Contracts"("Id") ON DELETE CASCADE,
    CONSTRAINT "CK_AssetRecurringContractLinks_Role" CHECK ("Role" IN ('hoa','property_tax','insurance','utilities','maintenance_plan','other'))
);
CREATE INDEX "IX_AssetRecurringContractLinks_AssetId" ON "AssetRecurringContractLinks"("AssetId");

CREATE OR REPLACE FUNCTION fullworth_validate_property_unit()
RETURNS trigger AS $$
DECLARE asset_space uuid;
BEGIN
    SELECT a."FullWorthSpaceId" INTO asset_space
    FROM "Assets" a JOIN "RealEstateAssetDetails" d ON d."AssetId"=a."Id"
    WHERE a."Id"=NEW."AssetId";
    IF asset_space IS NULL OR asset_space <> NEW."FullWorthSpaceId" THEN
        RAISE EXCEPTION 'Property unit must belong to the real-estate asset FullWorth Space';
    END IF;
    NEW."Name" := btrim(NEW."Name");
    NEW."UpdatedAt" := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_PropertyUnits_Validate" BEFORE INSERT OR UPDATE ON "PropertyUnits"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_property_unit();

CREATE OR REPLACE FUNCTION fullworth_validate_rental_lease()
RETURNS trigger AS $$
DECLARE unit_space uuid; unit_asset uuid;
BEGIN
    SELECT "FullWorthSpaceId", "AssetId" INTO unit_space, unit_asset FROM "PropertyUnits" WHERE "Id"=NEW."PropertyUnitId" FOR UPDATE;
    IF unit_space IS NULL OR unit_space <> NEW."FullWorthSpaceId" OR unit_asset <> NEW."AssetId" THEN
        RAISE EXCEPTION 'Rental lease unit must belong to the same property and FullWorth Space';
    END IF;
    IF NEW."Status"='active' AND EXISTS (
        SELECT 1 FROM "RentalLeases" l
        WHERE l."PropertyUnitId"=NEW."PropertyUnitId" AND l."Id"<>NEW."Id" AND l."Status"='active'
          AND daterange(l."StartDate", COALESCE(l."EndDate", 'infinity'::date), '[]') && daterange(NEW."StartDate", COALESCE(NEW."EndDate", 'infinity'::date), '[]')
    ) THEN
        RAISE EXCEPTION 'Active rental leases for one unit cannot overlap';
    END IF;
    NEW."Currency" := upper(btrim(NEW."Currency"));
    NEW."UpdatedAt" := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_RentalLeases_Validate" BEFORE INSERT OR UPDATE ON "RentalLeases"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_rental_lease();

CREATE OR REPLACE FUNCTION fullworth_validate_asset_cashflow()
RETURNS trigger AS $$
DECLARE asset_space uuid; tx_space uuid; tx_amount numeric(20,8); tx_currency varchar(3); tx_date date; allocated numeric(20,8);
BEGIN
    SELECT "FullWorthSpaceId" INTO asset_space FROM "Assets" WHERE "Id"=NEW."AssetId";
    IF asset_space IS NULL OR asset_space <> NEW."FullWorthSpaceId" THEN
        RAISE EXCEPTION 'Asset cashflow must use the asset FullWorth Space';
    END IF;
    NEW."Currency" := upper(btrim(NEW."Currency"));
    IF NEW."TransactionId" IS NOT NULL THEN
        SELECT a."FullWorthSpaceId", t."Amount", t."Currency", COALESCE(t."BookingDate", t."ValueDate", NEW."Date")
        INTO tx_space, tx_amount, tx_currency, tx_date
        FROM "Transactions" t JOIN "Accounts" a ON a."Id"=t."AccountId"
        WHERE t."Id"=NEW."TransactionId" FOR UPDATE OF t;
        IF tx_space IS NULL OR tx_space <> NEW."FullWorthSpaceId" THEN
            RAISE EXCEPTION 'Linked transaction must be in the same FullWorth Space';
        END IF;
        IF upper(tx_currency) <> NEW."Currency" THEN
            RAISE EXCEPTION 'Transaction-backed asset cashflow must use transaction currency';
        END IF;
        IF (tx_amount > 0 AND NEW."Direction" <> 'income') OR (tx_amount < 0 AND NEW."Direction" <> 'expense') THEN
            RAISE EXCEPTION 'Transaction-backed asset cashflow direction must match transaction direction';
        END IF;
        NEW."Date" := tx_date;
        SELECT COALESCE(SUM(c."Amount"),0) INTO allocated FROM "AssetCashflowEntries" c
        WHERE c."TransactionId"=NEW."TransactionId" AND c."Id"<>NEW."Id";
        IF allocated + NEW."Amount" > abs(tx_amount) THEN
            RAISE EXCEPTION 'Asset cashflow allocations cannot exceed transaction amount';
        END IF;
    END IF;
    NEW."UpdatedAt" := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_AssetCashflowEntries_Validate" BEFORE INSERT OR UPDATE ON "AssetCashflowEntries"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_asset_cashflow();

CREATE OR REPLACE FUNCTION fullworth_validate_improvement_cashflow()
RETURNS trigger AS $$
DECLARE improvement_asset uuid; cashflow_asset uuid;
BEGIN
    SELECT "AssetId" INTO improvement_asset FROM "PropertyImprovements" WHERE "Id"=NEW."ImprovementId";
    SELECT "AssetId" INTO cashflow_asset FROM "AssetCashflowEntries" WHERE "Id"=NEW."CashflowEntryId";
    IF improvement_asset IS NULL OR cashflow_asset IS NULL OR improvement_asset <> cashflow_asset THEN
        RAISE EXCEPTION 'Improvement cashflow must belong to the same asset';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_PropertyImprovementCashflows_Validate" BEFORE INSERT OR UPDATE ON "PropertyImprovementCashflows"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_improvement_cashflow();

CREATE OR REPLACE FUNCTION fullworth_validate_asset_contract_link()
RETURNS trigger AS $$
DECLARE asset_space uuid; contract_space uuid;
BEGIN
    SELECT "FullWorthSpaceId" INTO asset_space FROM "Assets" WHERE "Id"=NEW."AssetId";
    SELECT "FullWorthSpaceId" INTO contract_space FROM "Contracts" WHERE "Id"=NEW."RecurringContractId";
    IF asset_space IS NULL OR contract_space IS NULL OR asset_space <> NEW."FullWorthSpaceId" OR contract_space <> NEW."FullWorthSpaceId" THEN
        RAISE EXCEPTION 'Asset and recurring contract must belong to the same FullWorth Space';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_AssetRecurringContractLinks_Validate" BEFORE INSERT OR UPDATE ON "AssetRecurringContractLinks"
FOR EACH ROW EXECUTE FUNCTION fullworth_validate_asset_contract_link();

INSERT INTO "PropertyUnits" ("Id","FullWorthSpaceId","AssetId","Name","UnitType","AreaSqm","Rooms","OwnershipSharePercent","IsOwnerOccupied","IsActive","CreatedAt","UpdatedAt")
SELECT gen_random_uuid(), a."FullWorthSpaceId", d."AssetId", COALESCE(NULLIF(d."UnitLabel",''),'Haupteinheit'),
       CASE WHEN d."PropertyType"='commercial' THEN 'commercial' ELSE 'apartment' END,
       d."LivingAreaSqm", d."Rooms", d."OwnershipSharePercent", d."UsageType"='owner_occupied', true, now(), now()
FROM "RealEstateAssetDetails" d JOIN "Assets" a ON a."Id"=d."AssetId"
WHERE NOT EXISTS (SELECT 1 FROM "PropertyUnits" u WHERE u."AssetId"=d."AssetId");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_AssetRecurringContractLinks_Validate" ON "AssetRecurringContractLinks";
DROP FUNCTION IF EXISTS fullworth_validate_asset_contract_link();
DROP TRIGGER IF EXISTS "TR_PropertyImprovementCashflows_Validate" ON "PropertyImprovementCashflows";
DROP FUNCTION IF EXISTS fullworth_validate_improvement_cashflow();
DROP TRIGGER IF EXISTS "TR_AssetCashflowEntries_Validate" ON "AssetCashflowEntries";
DROP FUNCTION IF EXISTS fullworth_validate_asset_cashflow();
DROP TRIGGER IF EXISTS "TR_RentalLeases_Validate" ON "RentalLeases";
DROP FUNCTION IF EXISTS fullworth_validate_rental_lease();
DROP TRIGGER IF EXISTS "TR_PropertyUnits_Validate" ON "PropertyUnits";
DROP FUNCTION IF EXISTS fullworth_validate_property_unit();
DROP TABLE IF EXISTS "AssetRecurringContractLinks";
DROP TABLE IF EXISTS "PropertyImprovementCashflows";
DROP TABLE IF EXISTS "PropertyImprovements";
DROP TABLE IF EXISTS "AssetCashflowEntries";
DROP TABLE IF EXISTS "RentalLeases";
DROP TABLE IF EXISTS "PropertyUnits";
""");
    }
}
