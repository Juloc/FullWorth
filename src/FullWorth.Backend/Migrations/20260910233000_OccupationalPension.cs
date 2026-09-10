using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// The occupational pension (bAV) area, per docs/PENSION.md. Six tables, because a pension contract
/// carries facts that have no home in Contracts or Assets — implementation route, policy holder vs
/// insured person, guarantee quota and annuity factor, the employer/employee split, the security/fund
/// ratio, structured costs and a fund allocation — while the value itself keeps counting through the
/// existing Asset and the employee payment through the existing RecurringContract.
///
/// The constraints here are the ones the numbers depend on, so they hold even for a writer that
/// bypasses the store:
///
/// - the identity of a contract is provider + policy number, so an annual statement for a contract
///   that already exists can only ever add a snapshot (UX_BavContracts_PolicyIdentity);
/// - the identity of a snapshot is contract + date + document, with NULLS NOT DISTINCT so a second
///   hand-entered snapshot for the same date collides instead of quietly duplicating history;
/// - a tax or social-insurance effect cannot exist without the source that stated it
///   (CK_BavContributions_TaxEffectSource);
/// - an estimated cost cannot exist without saying how it was estimated (CK_BavCosts_Estimate);
/// - a projected figure cannot exist without its basis and its return assumption
///   (CK_BavSnapshots_Projection), so nothing projected can be mistaken for a guarantee.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260910233000_OccupationalPension")]
public sealed class OccupationalPension : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "BavContracts" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "ProviderName" character varying(200) NOT NULL,
    "ProviderKey" character varying(200) NOT NULL,
    "TariffName" character varying(200) NULL,
    -- Personal data: stored encrypted, matched through the blind index, displayed as the last four.
    "PolicyNumberEncrypted" character varying(500) NULL,
    "PolicyNumberLookup" character varying(128) NULL,
    "PolicyNumberLast4" character varying(8) NULL,
    "ImplementationRoute" character varying(32) NOT NULL,
    "Status" character varying(24) NOT NULL,
    "EmployerName" character varying(200) NULL,
    "PolicyHolderName" character varying(200) NULL,
    "InsuredPersonName" character varying(200) NULL,
    "StartDate" date NULL,
    "RetirementDate" date NULL,
    "ContractEndDate" date NULL,
    "Currency" character varying(3) NOT NULL,
    "GuaranteeQuotaPercent" numeric(9,4) NULL,
    "GuaranteedAnnuityFactor" numeric(12,4) NULL,
    "FundSelectionChangeable" boolean NOT NULL DEFAULT FALSE,
    "AssetId" uuid NULL,
    "RecurringContractId" uuid NULL,
    "Notes" character varying(2000) NULL,
    "CreatedByUserId" uuid NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_BavContracts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BavContracts_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BavContracts_Assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES "Assets" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavContracts_Contracts_RecurringContractId" FOREIGN KEY ("RecurringContractId") REFERENCES "Contracts" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavContracts_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_BavContracts_Route" CHECK ("ImplementationRoute" IN ('direct_insurance','pension_fund','pension_scheme','provident_fund','direct_commitment','other')),
    -- paid_up is beitragsfrei: its own state, listed next to active rather than folded into terminated.
    CONSTRAINT "CK_BavContracts_Status" CHECK ("Status" IN ('active','paid_up','in_payout','transferred','terminated')),
    CONSTRAINT "CK_BavContracts_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_BavContracts_GuaranteeQuota" CHECK ("GuaranteeQuotaPercent" IS NULL OR ("GuaranteeQuotaPercent" >= 0 AND "GuaranteeQuotaPercent" <= 100)),
    CONSTRAINT "CK_BavContracts_AnnuityFactor" CHECK ("GuaranteedAnnuityFactor" IS NULL OR "GuaranteedAnnuityFactor" >= 0)
);

CREATE INDEX IF NOT EXISTS "IX_BavContracts_FullWorthSpaceId" ON "BavContracts" ("FullWorthSpaceId");
CREATE INDEX IF NOT EXISTS "IX_BavContracts_FullWorthSpaceId_ProviderKey" ON "BavContracts" ("FullWorthSpaceId", "ProviderKey");
CREATE INDEX IF NOT EXISTS "IX_BavContracts_AssetId" ON "BavContracts" ("AssetId");
CREATE INDEX IF NOT EXISTS "IX_BavContracts_RecurringContractId" ON "BavContracts" ("RecurringContractId");
CREATE INDEX IF NOT EXISTS "IX_BavContracts_CreatedByUserId" ON "BavContracts" ("CreatedByUserId");
-- Existing-contract detection: the same policy number at the same provider IS the same contract, so a
-- second row for it cannot be created at all. Partial, because a hand-entered contract without a
-- number is not automatically the same contract as another one without a number.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_BavContracts_PolicyIdentity"
    ON "BavContracts" ("FullWorthSpaceId", "ProviderKey", "PolicyNumberLookup")
    WHERE "PolicyNumberLookup" IS NOT NULL;

CREATE TABLE IF NOT EXISTS "BavDocuments" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "BavContractId" uuid NULL,
    "Sha256" character varying(64) NOT NULL,
    "Kind" character varying(32) NOT NULL,
    "OriginalFileName" character varying(500) NULL,
    "MediaType" character varying(150) NULL,
    "ByteSize" bigint NOT NULL DEFAULT 0,
    "PageCount" integer NULL,
    "StoragePath" character varying(1000) NULL,
    "EncryptionScheme" character varying(32) NULL,
    "ExtractionStatus" character varying(24) NOT NULL,
    "ExtractionConfidence" numeric(5,4) NULL,
    "ExtractionSource" character varying(24) NULL,
    "ReviewedByUserId" uuid NULL,
    "ReviewedAt" timestamp with time zone NULL,
    "CreatedByUserId" uuid NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_BavDocuments" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BavDocuments_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BavDocuments_BavContracts_BavContractId" FOREIGN KEY ("BavContractId") REFERENCES "BavContracts" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavDocuments_Users_ReviewedByUserId" FOREIGN KEY ("ReviewedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavDocuments_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_BavDocuments_Kind" CHECK ("Kind" IN ('annual_statement','certificate','offer','correspondence','other')),
    CONSTRAINT "CK_BavDocuments_Status" CHECK ("ExtractionStatus" IN ('pending','parsed','reviewed','committed','failed')),
    CONSTRAINT "CK_BavDocuments_Source" CHECK ("ExtractionSource" IS NULL OR "ExtractionSource" IN ('deterministic','codex','manual')),
    CONSTRAINT "CK_BavDocuments_Confidence" CHECK ("ExtractionConfidence" IS NULL OR ("ExtractionConfidence" >= 0 AND "ExtractionConfidence" <= 1)),
    CONSTRAINT "CK_BavDocuments_ByteSize" CHECK ("ByteSize" >= 0)
);

CREATE INDEX IF NOT EXISTS "IX_BavDocuments_BavContractId" ON "BavDocuments" ("BavContractId");
CREATE INDEX IF NOT EXISTS "IX_BavDocuments_ReviewedByUserId" ON "BavDocuments" ("ReviewedByUserId");
CREATE INDEX IF NOT EXISTS "IX_BavDocuments_CreatedByUserId" ON "BavDocuments" ("CreatedByUserId");
-- The same file is never imported twice.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BavDocuments_FullWorthSpaceId_Sha256"
    ON "BavDocuments" ("FullWorthSpaceId", "Sha256");

CREATE TABLE IF NOT EXISTS "BavSnapshots" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "BavContractId" uuid NOT NULL,
    "EffectiveDate" date NOT NULL,
    "Currency" character varying(3) NOT NULL,
    "Balance" numeric(20,8) NULL,
    "GuaranteedBalance" numeric(20,8) NULL,
    "SurrenderValue" numeric(20,8) NULL,
    "SecurityAssetsAmount" numeric(20,8) NULL,
    "FundAssetsAmount" numeric(20,8) NULL,
    "GuaranteedCapitalAtRetirement" numeric(20,8) NULL,
    "GuaranteedMonthlyAnnuity" numeric(20,8) NULL,
    "ProjectedCapitalAtRetirement" numeric(20,8) NULL,
    "ProjectedMonthlyAnnuity" numeric(20,8) NULL,
    "ProjectionReturnPercent" numeric(9,4) NULL,
    "ProjectionBasis" character varying(24) NULL,
    "Source" character varying(16) NOT NULL,
    "BavDocumentId" uuid NULL,
    "DocumentSha256" character varying(64) NULL,
    "ExtractionConfidence" numeric(5,4) NULL,
    "IsCurrent" boolean NOT NULL DEFAULT FALSE,
    "Note" character varying(500) NULL,
    "CreatedByUserId" uuid NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_BavSnapshots" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BavSnapshots_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BavSnapshots_BavContracts_BavContractId" FOREIGN KEY ("BavContractId") REFERENCES "BavContracts" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_BavSnapshots_BavDocuments_BavDocumentId" FOREIGN KEY ("BavDocumentId") REFERENCES "BavDocuments" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavSnapshots_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_BavSnapshots_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_BavSnapshots_Source" CHECK ("Source" IN ('manual','document','payslip','import','provider')),
    CONSTRAINT "CK_BavSnapshots_Balance" CHECK ("Balance" IS NULL OR "Balance" >= 0),
    CONSTRAINT "CK_BavSnapshots_GuaranteedBalance" CHECK ("GuaranteedBalance" IS NULL OR ("GuaranteedBalance" >= 0 AND ("Balance" IS NULL OR "GuaranteedBalance" <= "Balance"))),
    CONSTRAINT "CK_BavSnapshots_SurrenderValue" CHECK ("SurrenderValue" IS NULL OR "SurrenderValue" >= 0),
    CONSTRAINT "CK_BavSnapshots_SplitAmounts" CHECK (("SecurityAssetsAmount" IS NULL OR "SecurityAssetsAmount" >= 0) AND ("FundAssetsAmount" IS NULL OR "FundAssetsAmount" >= 0)),
    CONSTRAINT "CK_BavSnapshots_Retirement" CHECK (("GuaranteedCapitalAtRetirement" IS NULL OR "GuaranteedCapitalAtRetirement" >= 0) AND ("GuaranteedMonthlyAnnuity" IS NULL OR "GuaranteedMonthlyAnnuity" >= 0) AND ("ProjectedCapitalAtRetirement" IS NULL OR "ProjectedCapitalAtRetirement" >= 0) AND ("ProjectedMonthlyAnnuity" IS NULL OR "ProjectedMonthlyAnnuity" >= 0)),
    CONSTRAINT "CK_BavSnapshots_ProjectionBasis" CHECK ("ProjectionBasis" IS NULL OR "ProjectionBasis" IN ('document_guaranteed','document_forecast','simulation')),
    -- A projection may not be stored bare: without a basis and a return assumption it would be
    -- indistinguishable from a guarantee on screen.
    CONSTRAINT "CK_BavSnapshots_Projection" CHECK (
        ("ProjectedCapitalAtRetirement" IS NULL AND "ProjectedMonthlyAnnuity" IS NULL)
        OR ("ProjectionBasis" IS NOT NULL AND "ProjectionReturnPercent" IS NOT NULL)),
    CONSTRAINT "CK_BavSnapshots_Confidence" CHECK ("ExtractionConfidence" IS NULL OR ("ExtractionConfidence" >= 0 AND "ExtractionConfidence" <= 1))
);

CREATE INDEX IF NOT EXISTS "IX_BavSnapshots_FullWorthSpaceId" ON "BavSnapshots" ("FullWorthSpaceId");
CREATE INDEX IF NOT EXISTS "IX_BavSnapshots_BavDocumentId" ON "BavSnapshots" ("BavDocumentId");
CREATE INDEX IF NOT EXISTS "IX_BavSnapshots_CreatedByUserId" ON "BavSnapshots" ("CreatedByUserId");
CREATE INDEX IF NOT EXISTS "IX_BavSnapshots_BavContractId_EffectiveDate"
    ON "BavSnapshots" ("BavContractId", "EffectiveDate" DESC);
-- (contract, date, document) is the snapshot identity from docs/PENSION.md. NULLS NOT DISTINCT so a
-- hand-entered snapshot for a date that already has one is a conflict the caller has to resolve,
-- rather than a second row nobody can tell apart. Re-reading the same document produces nothing.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_BavSnapshots_Identity"
    ON "BavSnapshots" ("BavContractId", "EffectiveDate", "DocumentSha256") NULLS NOT DISTINCT;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_BavSnapshots_Current"
    ON "BavSnapshots" ("BavContractId") WHERE "IsCurrent";

CREATE TABLE IF NOT EXISTS "BavContributions" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "BavContractId" uuid NOT NULL,
    "ValidFrom" date NOT NULL,
    "ValidUntil" date NULL,
    "EndReason" character varying(24) NULL,
    "Cycle" character varying(16) NOT NULL,
    "Currency" character varying(3) NOT NULL,
    "EmployeeAmount" numeric(20,8) NOT NULL DEFAULT 0,
    "EmployerSubsidyAmount" numeric(20,8) NOT NULL DEFAULT 0,
    "EmployerAmount" numeric(20,8) NOT NULL DEFAULT 0,
    "StatedTotalAmount" numeric(20,8) NULL,
    "Source" character varying(16) NOT NULL,
    "TaxSavingAmount" numeric(20,8) NULL,
    "SocialSecuritySavingAmount" numeric(20,8) NULL,
    "NetEffortAmount" numeric(20,8) NULL,
    "TaxEffectSource" character varying(16) NULL,
    "TaxEffectSourceReference" character varying(200) NULL,
    "BavDocumentId" uuid NULL,
    "Note" character varying(500) NULL,
    "CreatedByUserId" uuid NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_BavContributions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BavContributions_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BavContributions_BavContracts_BavContractId" FOREIGN KEY ("BavContractId") REFERENCES "BavContracts" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_BavContributions_BavDocuments_BavDocumentId" FOREIGN KEY ("BavDocumentId") REFERENCES "BavDocuments" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavContributions_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_BavContributions_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_BavContributions_Cycle" CHECK ("Cycle" IN ('monthly','quarterly','semiannual','yearly','one_off')),
    CONSTRAINT "CK_BavContributions_Source" CHECK ("Source" IN ('manual','document','payslip','import','provider')),
    CONSTRAINT "CK_BavContributions_Shares" CHECK ("EmployeeAmount" >= 0 AND "EmployerSubsidyAmount" >= 0 AND "EmployerAmount" >= 0),
    CONSTRAINT "CK_BavContributions_StatedTotal" CHECK ("StatedTotalAmount" IS NULL OR "StatedTotalAmount" >= 0),
    CONSTRAINT "CK_BavContributions_Period" CHECK ("ValidUntil" IS NULL OR "ValidUntil" >= "ValidFrom"),
    CONSTRAINT "CK_BavContributions_EndReason" CHECK (("EndReason" IS NULL) OR ("ValidUntil" IS NOT NULL AND "EndReason" IN ('paid_up','employer_change','amount_change','payout','terminated'))),
    -- FullWorth does not compute a personal tax effect. A stored one names what stated it, and its own
    -- arithmetic is stored as 'simulation' and labelled as one.
    CONSTRAINT "CK_BavContributions_TaxEffectSource" CHECK (
        ("TaxSavingAmount" IS NULL AND "SocialSecuritySavingAmount" IS NULL AND "NetEffortAmount" IS NULL)
        OR "TaxEffectSource" IN ('document','payslip','simulation')),
    CONSTRAINT "CK_BavContributions_TaxEffectAmounts" CHECK (
        ("TaxSavingAmount" IS NULL OR "TaxSavingAmount" >= 0)
        AND ("SocialSecuritySavingAmount" IS NULL OR "SocialSecuritySavingAmount" >= 0)
        AND ("NetEffortAmount" IS NULL OR "NetEffortAmount" >= 0))
);

CREATE INDEX IF NOT EXISTS "IX_BavContributions_FullWorthSpaceId" ON "BavContributions" ("FullWorthSpaceId");
CREATE INDEX IF NOT EXISTS "IX_BavContributions_BavDocumentId" ON "BavContributions" ("BavDocumentId");
CREATE INDEX IF NOT EXISTS "IX_BavContributions_CreatedByUserId" ON "BavContributions" ("CreatedByUserId");
CREATE INDEX IF NOT EXISTS "IX_BavContributions_BavContractId_ValidFrom"
    ON "BavContributions" ("BavContractId", "ValidFrom" DESC);

CREATE TABLE IF NOT EXISTS "BavInvestmentAllocations" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "BavContractId" uuid NOT NULL,
    "BavSnapshotId" uuid NULL,
    "EffectiveDate" date NOT NULL,
    "FundName" character varying(300) NOT NULL,
    "Isin" character varying(12) NULL,
    "WeightPercent" numeric(9,4) NULL,
    "Amount" numeric(20,8) NULL,
    "Currency" character varying(3) NOT NULL,
    "OngoingChargesPercent" numeric(9,4) NULL,
    "OngoingChargesEstimated" boolean NOT NULL DEFAULT FALSE,
    "AssetClass" character varying(24) NOT NULL,
    "Source" character varying(16) NOT NULL,
    "Note" character varying(500) NULL,
    "CreatedByUserId" uuid NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_BavInvestmentAllocations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BavInvestmentAllocations_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BavInvestmentAllocations_BavContracts_BavContractId" FOREIGN KEY ("BavContractId") REFERENCES "BavContracts" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_BavInvestmentAllocations_BavSnapshots_BavSnapshotId" FOREIGN KEY ("BavSnapshotId") REFERENCES "BavSnapshots" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavInvestmentAllocations_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_BavInvestmentAllocations_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_BavInvestmentAllocations_Isin" CHECK ("Isin" IS NULL OR "Isin" ~ '^[A-Z]{2}[A-Z0-9]{10}$'),
    CONSTRAINT "CK_BavInvestmentAllocations_Weight" CHECK ("WeightPercent" IS NULL OR ("WeightPercent" >= 0 AND "WeightPercent" <= 100)),
    CONSTRAINT "CK_BavInvestmentAllocations_Amount" CHECK ("Amount" IS NULL OR "Amount" >= 0),
    CONSTRAINT "CK_BavInvestmentAllocations_Charges" CHECK ("OngoingChargesPercent" IS NULL OR ("OngoingChargesPercent" >= 0 AND "OngoingChargesPercent" <= 100)),
    CONSTRAINT "CK_BavInvestmentAllocations_AssetClass" CHECK ("AssetClass" IN ('equity','bond','mixed','money_market','real_estate','commodity','guarantee_assets','other')),
    CONSTRAINT "CK_BavInvestmentAllocations_Source" CHECK ("Source" IN ('manual','document','payslip','import','provider'))
);

CREATE INDEX IF NOT EXISTS "IX_BavInvestmentAllocations_FullWorthSpaceId" ON "BavInvestmentAllocations" ("FullWorthSpaceId");
CREATE INDEX IF NOT EXISTS "IX_BavInvestmentAllocations_BavSnapshotId" ON "BavInvestmentAllocations" ("BavSnapshotId");
CREATE INDEX IF NOT EXISTS "IX_BavInvestmentAllocations_CreatedByUserId" ON "BavInvestmentAllocations" ("CreatedByUserId");
CREATE INDEX IF NOT EXISTS "IX_BavInvestmentAllocations_BavContractId_EffectiveDate"
    ON "BavInvestmentAllocations" ("BavContractId", "EffectiveDate" DESC);

CREATE TABLE IF NOT EXISTS "BavCosts" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "BavContractId" uuid NOT NULL,
    "BavSnapshotId" uuid NULL,
    "EffectiveDate" date NOT NULL,
    "AppliesUntilDate" date NULL,
    "Kind" character varying(40) NOT NULL,
    "Basis" character varying(32) NOT NULL,
    "Amount" numeric(20,8) NULL,
    "Currency" character varying(3) NOT NULL,
    "Percent" numeric(9,4) NULL,
    "Timing" character varying(16) NOT NULL,
    "IsEstimated" boolean NOT NULL DEFAULT FALSE,
    "EstimateBasis" character varying(300) NULL,
    -- Beitragsfrei is not cost-free: a cost that keeps running on the capital says so here.
    "ContinuesWhenPaidUp" boolean NOT NULL DEFAULT TRUE,
    "Source" character varying(16) NOT NULL,
    "BavDocumentId" uuid NULL,
    "Note" character varying(500) NULL,
    "CreatedByUserId" uuid NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_BavCosts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_BavCosts_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_BavCosts_BavContracts_BavContractId" FOREIGN KEY ("BavContractId") REFERENCES "BavContracts" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_BavCosts_BavSnapshots_BavSnapshotId" FOREIGN KEY ("BavSnapshotId") REFERENCES "BavSnapshots" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavCosts_BavDocuments_BavDocumentId" FOREIGN KEY ("BavDocumentId") REFERENCES "BavDocuments" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_BavCosts_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_BavCosts_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_BavCosts_Kind" CHECK ("Kind" IN ('acquisition','administration_on_contribution','administration_on_capital','administration_fixed','fund','guarantee','risk_premium','payout','other')),
    CONSTRAINT "CK_BavCosts_Basis" CHECK ("Basis" IN ('fixed_amount','percent_of_contribution','percent_of_capital','percent_of_sum','percent_of_annuity')),
    CONSTRAINT "CK_BavCosts_Timing" CHECK ("Timing" IN ('incurred','ongoing','future')),
    CONSTRAINT "CK_BavCosts_Source" CHECK ("Source" IN ('manual','document','payslip','import','provider')),
    CONSTRAINT "CK_BavCosts_Amount" CHECK ("Amount" IS NULL OR "Amount" >= 0),
    CONSTRAINT "CK_BavCosts_Percent" CHECK ("Percent" IS NULL OR ("Percent" >= 0 AND "Percent" <= 100)),
    CONSTRAINT "CK_BavCosts_Period" CHECK ("AppliesUntilDate" IS NULL OR "AppliesUntilDate" >= "EffectiveDate"),
    -- A cost figure has to be a figure of something: a fixed cost carries an amount, a percentage cost
    -- carries a percentage.
    CONSTRAINT "CK_BavCosts_Figure" CHECK (
        ("Basis" = 'fixed_amount' AND "Amount" IS NOT NULL AND "Percent" IS NULL)
        OR ("Basis" <> 'fixed_amount' AND "Percent" IS NOT NULL AND "Amount" IS NULL)),
    -- An estimate says how it was estimated, so it can never be shown as a contract value.
    CONSTRAINT "CK_BavCosts_Estimate" CHECK (NOT "IsEstimated" OR "EstimateBasis" IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS "IX_BavCosts_FullWorthSpaceId" ON "BavCosts" ("FullWorthSpaceId");
CREATE INDEX IF NOT EXISTS "IX_BavCosts_BavSnapshotId" ON "BavCosts" ("BavSnapshotId");
CREATE INDEX IF NOT EXISTS "IX_BavCosts_BavDocumentId" ON "BavCosts" ("BavDocumentId");
CREATE INDEX IF NOT EXISTS "IX_BavCosts_CreatedByUserId" ON "BavCosts" ("CreatedByUserId");
CREATE INDEX IF NOT EXISTS "IX_BavCosts_BavContractId_EffectiveDate"
    ON "BavCosts" ("BavContractId", "EffectiveDate" DESC);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "BavCosts";
DROP TABLE IF EXISTS "BavInvestmentAllocations";
DROP TABLE IF EXISTS "BavContributions";
DROP TABLE IF EXISTS "BavSnapshots";
DROP TABLE IF EXISTS "BavDocuments";
DROP TABLE IF EXISTS "BavContracts";
""");
    }
}
