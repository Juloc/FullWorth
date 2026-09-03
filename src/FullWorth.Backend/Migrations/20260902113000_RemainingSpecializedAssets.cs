using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260902113000_RemainingSpecializedAssets")]
public sealed class RemainingSpecializedAssets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE "CollectibleAssetDetails" (
    "AssetId" uuid PRIMARY KEY REFERENCES "Assets"("Id") ON DELETE CASCADE,
    "Category" varchar(32) NOT NULL,
    "Maker" varchar(160) NULL,
    "Model" varchar(160) NULL,
    "SerialNumber" varchar(160) NULL,
    "Condition" varchar(64) NULL,
    "PurchaseDate" date NULL,
    "PurchasePrice" numeric(20,8) NULL,
    "PurchaseCurrency" varchar(3) NULL,
    "InsuredValue" numeric(20,8) NULL,
    "AppraisedValue" numeric(20,8) NULL,
    "AppraisedAt" date NULL,
    "ProvenanceNotes" varchar(4000) NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_CollectibleAssetDetails_Category" CHECK ("Category" IN ('watch','jewelry','art','trading_card','wine','instrument','electronics','other')),
    CONSTRAINT "CK_CollectibleAssetDetails_Values" CHECK (("PurchasePrice" IS NULL OR "PurchasePrice" >= 0) AND ("InsuredValue" IS NULL OR "InsuredValue" >= 0) AND ("AppraisedValue" IS NULL OR "AppraisedValue" >= 0)),
    CONSTRAINT "CK_CollectibleAssetDetails_Currency" CHECK ("PurchaseCurrency" IS NULL OR "PurchaseCurrency" ~ '^[A-Z]{3}$')
);

CREATE TABLE "ReceivableAssetDetails" (
    "AssetId" uuid PRIMARY KEY REFERENCES "Assets"("Id") ON DELETE CASCADE,
    "CounterpartyDisplayLabel" varchar(200) NOT NULL,
    "OriginalPrincipal" numeric(20,8) NOT NULL,
    "OutstandingPrincipal" numeric(20,8) NOT NULL,
    "Currency" varchar(3) NOT NULL,
    "InterestRate" numeric(12,6) NULL,
    "StartDate" date NULL,
    "DueDate" date NULL,
    "PaymentCycle" varchar(20) NULL,
    "ExpectedPayment" numeric(20,8) NULL,
    "Status" varchar(20) NOT NULL DEFAULT 'active',
    "Notes" varchar(2000) NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_ReceivableAssetDetails_Principal" CHECK ("OriginalPrincipal" >= 0 AND "OutstandingPrincipal" >= 0 AND "OutstandingPrincipal" <= "OriginalPrincipal"),
    CONSTRAINT "CK_ReceivableAssetDetails_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_ReceivableAssetDetails_Interest" CHECK ("InterestRate" IS NULL OR "InterestRate" >= 0),
    CONSTRAINT "CK_ReceivableAssetDetails_ExpectedPayment" CHECK ("ExpectedPayment" IS NULL OR "ExpectedPayment" >= 0),
    CONSTRAINT "CK_ReceivableAssetDetails_Cycle" CHECK ("PaymentCycle" IS NULL OR "PaymentCycle" IN ('weekly','monthly','quarterly','yearly','one_time','other')),
    CONSTRAINT "CK_ReceivableAssetDetails_Status" CHECK ("Status" IN ('active','overdue','settled','written_off'))
);

CREATE TABLE "ReceivablePayments" (
    "Id" uuid PRIMARY KEY,
    "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE RESTRICT,
    "AssetId" uuid NOT NULL REFERENCES "ReceivableAssetDetails"("AssetId") ON DELETE CASCADE,
    "TransactionId" uuid NULL REFERENCES "Transactions"("Id") ON DELETE SET NULL,
    "Date" date NOT NULL,
    "PrincipalAmount" numeric(20,8) NOT NULL,
    "InterestAmount" numeric(20,8) NOT NULL DEFAULT 0,
    "Currency" varchar(3) NOT NULL,
    "Notes" varchar(1000) NULL,
    "CreatedByUserId" uuid NULL REFERENCES "Users"("Id") ON DELETE SET NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_ReceivablePayments_Amounts" CHECK ("PrincipalAmount" >= 0 AND "InterestAmount" >= 0 AND ("PrincipalAmount" > 0 OR "InterestAmount" > 0)),
    CONSTRAINT "CK_ReceivablePayments_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$')
);
CREATE INDEX "IX_ReceivablePayments_FullWorthSpaceId" ON "ReceivablePayments"("FullWorthSpaceId");
CREATE INDEX "IX_ReceivablePayments_AssetId_Date" ON "ReceivablePayments"("AssetId", "Date" DESC, "CreatedAt" DESC);
CREATE INDEX "IX_ReceivablePayments_TransactionId" ON "ReceivablePayments"("TransactionId");
CREATE UNIQUE INDEX "UX_ReceivablePayments_Asset_Transaction" ON "ReceivablePayments"("AssetId", "TransactionId") WHERE "TransactionId" IS NOT NULL;

CREATE TABLE "BusinessInterestAssetDetails" (
    "AssetId" uuid PRIMARY KEY REFERENCES "Assets"("Id") ON DELETE CASCADE,
    "CompanyDisplayName" varchar(240) NOT NULL,
    "LegalForm" varchar(80) NULL,
    "OwnershipPercent" numeric(12,6) NULL,
    "AcquisitionDate" date NULL,
    "InvestedCapital" numeric(20,8) NULL,
    "InvestedCurrency" varchar(3) NULL,
    "ValuationMethod" varchar(32) NULL,
    "LastDistributionDate" date NULL,
    "Notes" varchar(3000) NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_BusinessInterestAssetDetails_Ownership" CHECK ("OwnershipPercent" IS NULL OR ("OwnershipPercent" >= 0 AND "OwnershipPercent" <= 100)),
    CONSTRAINT "CK_BusinessInterestAssetDetails_Capital" CHECK ("InvestedCapital" IS NULL OR "InvestedCapital" >= 0),
    CONSTRAINT "CK_BusinessInterestAssetDetails_Currency" CHECK ("InvestedCurrency" IS NULL OR "InvestedCurrency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_BusinessInterestAssetDetails_Method" CHECK ("ValuationMethod" IS NULL OR "ValuationMethod" IN ('manual','last_financing','earnings_multiple','book_value','external_appraisal','other'))
);

CREATE TABLE "InsurancePensionAssetDetails" (
    "AssetId" uuid PRIMARY KEY REFERENCES "Assets"("Id") ON DELETE CASCADE,
    "ProviderName" varchar(200) NULL,
    "ProductName" varchar(200) NULL,
    "ProductType" varchar(32) NOT NULL,
    "PolicyReference" varchar(200) NULL,
    "StartDate" date NULL,
    "MaturityDate" date NULL,
    "RegularContribution" numeric(20,8) NULL,
    "ContributionCycle" varchar(20) NULL,
    "GuaranteedValue" numeric(20,8) NULL,
    "GuaranteedValueDate" date NULL,
    "Notes" varchar(3000) NULL,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "CK_InsurancePensionAssetDetails_Type" CHECK ("ProductType" IN ('pension','life_insurance','endowment','other')),
    CONSTRAINT "CK_InsurancePensionAssetDetails_Contribution" CHECK ("RegularContribution" IS NULL OR "RegularContribution" >= 0),
    CONSTRAINT "CK_InsurancePensionAssetDetails_Cycle" CHECK ("ContributionCycle" IS NULL OR "ContributionCycle" IN ('weekly','monthly','quarterly','yearly','other')),
    CONSTRAINT "CK_InsurancePensionAssetDetails_Guaranteed" CHECK ("GuaranteedValue" IS NULL OR "GuaranteedValue" >= 0)
);

CREATE OR REPLACE FUNCTION fullworth_validate_remaining_specialized_kind() RETURNS trigger AS $$
DECLARE asset_kind text;
BEGIN
    SELECT "Kind" INTO asset_kind FROM "Assets" WHERE "Id" = NEW."AssetId";
    IF TG_TABLE_NAME = 'CollectibleAssetDetails' AND asset_kind <> 'collectible' THEN RAISE EXCEPTION 'Collectible details require collectible asset'; END IF;
    IF TG_TABLE_NAME = 'ReceivableAssetDetails' AND asset_kind <> 'receivable' THEN RAISE EXCEPTION 'Receivable details require receivable asset'; END IF;
    IF TG_TABLE_NAME = 'BusinessInterestAssetDetails' AND asset_kind <> 'business_interest' THEN RAISE EXCEPTION 'Business-interest details require business-interest asset'; END IF;
    IF TG_TABLE_NAME = 'InsurancePensionAssetDetails' AND asset_kind <> 'insurance_pension' THEN RAISE EXCEPTION 'Insurance/pension details require insurance/pension asset'; END IF;
    NEW."UpdatedAt" = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER "TR_CollectibleAssetDetails_Kind" BEFORE INSERT OR UPDATE ON "CollectibleAssetDetails" FOR EACH ROW EXECUTE FUNCTION fullworth_validate_remaining_specialized_kind();
CREATE TRIGGER "TR_ReceivableAssetDetails_Kind" BEFORE INSERT OR UPDATE ON "ReceivableAssetDetails" FOR EACH ROW EXECUTE FUNCTION fullworth_validate_remaining_specialized_kind();
CREATE TRIGGER "TR_BusinessInterestAssetDetails_Kind" BEFORE INSERT OR UPDATE ON "BusinessInterestAssetDetails" FOR EACH ROW EXECUTE FUNCTION fullworth_validate_remaining_specialized_kind();
CREATE TRIGGER "TR_InsurancePensionAssetDetails_Kind" BEFORE INSERT OR UPDATE ON "InsurancePensionAssetDetails" FOR EACH ROW EXECUTE FUNCTION fullworth_validate_remaining_specialized_kind();

CREATE OR REPLACE FUNCTION fullworth_protect_remaining_specialized_kind() RETURNS trigger AS $$
BEGIN
    IF OLD."Kind" = NEW."Kind" THEN RETURN NEW; END IF;
    IF OLD."Kind" = 'collectible' AND EXISTS (SELECT 1 FROM "CollectibleAssetDetails" WHERE "AssetId" = OLD."Id") THEN RAISE EXCEPTION 'Remove collectible details before changing asset kind'; END IF;
    IF OLD."Kind" = 'receivable' AND EXISTS (SELECT 1 FROM "ReceivableAssetDetails" WHERE "AssetId" = OLD."Id") THEN RAISE EXCEPTION 'Remove receivable details before changing asset kind'; END IF;
    IF OLD."Kind" = 'business_interest' AND EXISTS (SELECT 1 FROM "BusinessInterestAssetDetails" WHERE "AssetId" = OLD."Id") THEN RAISE EXCEPTION 'Remove business-interest details before changing asset kind'; END IF;
    IF OLD."Kind" = 'insurance_pension' AND EXISTS (SELECT 1 FROM "InsurancePensionAssetDetails" WHERE "AssetId" = OLD."Id") THEN RAISE EXCEPTION 'Remove insurance/pension details before changing asset kind'; END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_Assets_ProtectRemainingSpecializedKind" BEFORE UPDATE OF "Kind" ON "Assets" FOR EACH ROW EXECUTE FUNCTION fullworth_protect_remaining_specialized_kind();

CREATE OR REPLACE FUNCTION fullworth_validate_receivable_payment_space() RETURNS trigger AS $$
DECLARE asset_space uuid; transaction_space uuid;
BEGIN
    SELECT a."FullWorthSpaceId" INTO asset_space FROM "Assets" a WHERE a."Id" = NEW."AssetId";
    IF asset_space IS NULL OR asset_space <> NEW."FullWorthSpaceId" THEN RAISE EXCEPTION 'Receivable payment asset must belong to the same FullWorth Space'; END IF;
    IF NEW."TransactionId" IS NOT NULL THEN
        SELECT a."FullWorthSpaceId" INTO transaction_space FROM "Transactions" t JOIN "Accounts" a ON a."Id" = t."AccountId" WHERE t."Id" = NEW."TransactionId";
        IF transaction_space IS NULL OR transaction_space <> NEW."FullWorthSpaceId" THEN RAISE EXCEPTION 'Receivable payment transaction must belong to the same FullWorth Space'; END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER "TR_ReceivablePayments_Space" BEFORE INSERT OR UPDATE ON "ReceivablePayments" FOR EACH ROW EXECUTE FUNCTION fullworth_validate_receivable_payment_space();
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_ReceivablePayments_Space" ON "ReceivablePayments";
DROP FUNCTION IF EXISTS fullworth_validate_receivable_payment_space();
DROP TRIGGER IF EXISTS "TR_Assets_ProtectRemainingSpecializedKind" ON "Assets";
DROP FUNCTION IF EXISTS fullworth_protect_remaining_specialized_kind();
DROP TRIGGER IF EXISTS "TR_CollectibleAssetDetails_Kind" ON "CollectibleAssetDetails";
DROP TRIGGER IF EXISTS "TR_ReceivableAssetDetails_Kind" ON "ReceivableAssetDetails";
DROP TRIGGER IF EXISTS "TR_BusinessInterestAssetDetails_Kind" ON "BusinessInterestAssetDetails";
DROP TRIGGER IF EXISTS "TR_InsurancePensionAssetDetails_Kind" ON "InsurancePensionAssetDetails";
DROP FUNCTION IF EXISTS fullworth_validate_remaining_specialized_kind();
DROP TABLE IF EXISTS "ReceivablePayments";
DROP TABLE IF EXISTS "InsurancePensionAssetDetails";
DROP TABLE IF EXISTS "BusinessInterestAssetDetails";
DROP TABLE IF EXISTS "ReceivableAssetDetails";
DROP TABLE IF EXISTS "CollectibleAssetDetails";
""");
}
