using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260901203000_AssetValuationHistory")]
public sealed class AssetValuationHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "Assets" WHERE "CurrentValue" < 0) THEN
        RAISE EXCEPTION 'Cannot enable asset valuation history while Assets contains negative CurrentValue rows.';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS "AssetValuations" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "AssetId" uuid NOT NULL,
    "Amount" numeric(20,8) NOT NULL,
    "Currency" character varying(3) NOT NULL,
    "ValuedAt" date NOT NULL,
    "Method" character varying(32) NOT NULL,
    "LowEstimate" numeric(20,8) NULL,
    "HighEstimate" numeric(20,8) NULL,
    "Confidence" numeric(8,6) NULL,
    "ProviderKey" character varying(100) NULL,
    "ProviderDisplayName" character varying(200) NULL,
    "ExternalReference" character varying(500) NULL,
    "InputSummaryJson" jsonb NULL,
    "IsCurrent" boolean NOT NULL DEFAULT FALSE,
    "IsAccepted" boolean NOT NULL DEFAULT FALSE,
    "CreatedByUserId" uuid NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_AssetValuations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AssetValuations_Assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES "Assets" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_AssetValuations_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AssetValuations_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_AssetValuations_Amount" CHECK ("Amount" >= 0),
    CONSTRAINT "CK_AssetValuations_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_AssetValuations_Method" CHECK ("Method" IN ('manual','purchase_price','internal_estimate','external_provider','appraisal','import','legacy')),
    CONSTRAINT "CK_AssetValuations_LowEstimate" CHECK ("LowEstimate" IS NULL OR ("LowEstimate" >= 0 AND "LowEstimate" <= "Amount")),
    CONSTRAINT "CK_AssetValuations_HighEstimate" CHECK ("HighEstimate" IS NULL OR ("HighEstimate" >= 0 AND "HighEstimate" >= "Amount")),
    CONSTRAINT "CK_AssetValuations_EstimateRange" CHECK ("LowEstimate" IS NULL OR "HighEstimate" IS NULL OR "LowEstimate" <= "HighEstimate"),
    CONSTRAINT "CK_AssetValuations_Confidence" CHECK ("Confidence" IS NULL OR ("Confidence" >= 0 AND "Confidence" <= 1)),
    CONSTRAINT "CK_AssetValuations_CurrentAccepted" CHECK (NOT "IsCurrent" OR "IsAccepted")
);

CREATE INDEX IF NOT EXISTS "IX_AssetValuations_FullWorthSpaceId"
    ON "AssetValuations" ("FullWorthSpaceId");
CREATE INDEX IF NOT EXISTS "IX_AssetValuations_AssetId_ValuedAt"
    ON "AssetValuations" ("AssetId", "ValuedAt" DESC, "CreatedAt" DESC);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_AssetValuations_CurrentAccepted"
    ON "AssetValuations" ("AssetId")
    WHERE "IsCurrent" = TRUE AND "IsAccepted" = TRUE;

-- Backfill one immutable legacy valuation before normalizing old free-form kind values.
INSERT INTO "AssetValuations"
    ("Id", "FullWorthSpaceId", "AssetId", "Amount", "Currency", "ValuedAt", "Method",
     "InputSummaryJson", "IsCurrent", "IsAccepted", "CreatedByUserId", "CreatedAt")
SELECT
    gen_random_uuid(),
    a."FullWorthSpaceId",
    a."Id",
    a."CurrentValue",
    upper(a."Currency"),
    COALESCE(a."ValuedAt", a."UpdatedAt"::date, a."CreatedAt"::date, CURRENT_DATE),
    'legacy',
    jsonb_build_object('legacyKind', a."Kind"),
    TRUE,
    TRUE,
    NULL,
    now()
FROM "Assets" a
WHERE NOT EXISTS (SELECT 1 FROM "AssetValuations" v WHERE v."AssetId" = a."Id");

-- Keep the compatibility cache aligned with the initial history row.
UPDATE "Assets"
SET "Currency" = upper("Currency"),
    "ValuedAt" = COALESCE("ValuedAt", "UpdatedAt"::date, "CreatedAt"::date, CURRENT_DATE);

UPDATE "Assets"
SET "Kind" = CASE lower(btrim(COALESCE("Kind", '')))
    WHEN 'real_estate' THEN 'real_estate'
    WHEN 'property' THEN 'real_estate'
    WHEN 'realestate' THEN 'real_estate'
    WHEN 'immobilie' THEN 'real_estate'
    WHEN 'house' THEN 'real_estate'
    WHEN 'apartment' THEN 'real_estate'
    WHEN 'vehicle' THEN 'vehicle'
    WHEN 'car' THEN 'vehicle'
    WHEN 'auto' THEN 'vehicle'
    WHEN 'motorcycle' THEN 'vehicle'
    WHEN 'precious_metal' THEN 'precious_metal'
    WHEN 'gold' THEN 'precious_metal'
    WHEN 'silver' THEN 'precious_metal'
    WHEN 'metal' THEN 'precious_metal'
    WHEN 'collectible' THEN 'collectible'
    WHEN 'collection' THEN 'collectible'
    WHEN 'luxury' THEN 'collectible'
    WHEN 'receivable' THEN 'receivable'
    WHEN 'loan_receivable' THEN 'receivable'
    WHEN 'private_loan' THEN 'receivable'
    WHEN 'business_interest' THEN 'business_interest'
    WHEN 'business' THEN 'business_interest'
    WHEN 'company' THEN 'business_interest'
    WHEN 'equity' THEN 'business_interest'
    WHEN 'insurance_pension' THEN 'insurance_pension'
    WHEN 'insurance' THEN 'insurance_pension'
    WHEN 'pension' THEN 'insurance_pension'
    WHEN 'other' THEN 'other'
    ELSE 'other'
END;

ALTER TABLE "Assets" DROP CONSTRAINT IF EXISTS "CK_Assets_Kind";
ALTER TABLE "Assets" ADD CONSTRAINT "CK_Assets_Kind" CHECK (
    "Kind" IN ('real_estate','vehicle','precious_metal','collectible','receivable','business_interest','insurance_pension','other')
);

CREATE OR REPLACE FUNCTION fullworth_prepare_asset()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    normalized text;
BEGIN
    normalized := lower(btrim(COALESCE(NEW."Kind", '')));
    NEW."Kind" := CASE normalized
        WHEN 'real_estate' THEN 'real_estate'
        WHEN 'property' THEN 'real_estate'
        WHEN 'realestate' THEN 'real_estate'
        WHEN 'immobilie' THEN 'real_estate'
        WHEN 'house' THEN 'real_estate'
        WHEN 'apartment' THEN 'real_estate'
        WHEN 'vehicle' THEN 'vehicle'
        WHEN 'car' THEN 'vehicle'
        WHEN 'auto' THEN 'vehicle'
        WHEN 'motorcycle' THEN 'vehicle'
        WHEN 'precious_metal' THEN 'precious_metal'
        WHEN 'gold' THEN 'precious_metal'
        WHEN 'silver' THEN 'precious_metal'
        WHEN 'metal' THEN 'precious_metal'
        WHEN 'collectible' THEN 'collectible'
        WHEN 'collection' THEN 'collectible'
        WHEN 'luxury' THEN 'collectible'
        WHEN 'receivable' THEN 'receivable'
        WHEN 'loan_receivable' THEN 'receivable'
        WHEN 'private_loan' THEN 'receivable'
        WHEN 'business_interest' THEN 'business_interest'
        WHEN 'business' THEN 'business_interest'
        WHEN 'company' THEN 'business_interest'
        WHEN 'equity' THEN 'business_interest'
        WHEN 'insurance_pension' THEN 'insurance_pension'
        WHEN 'insurance' THEN 'insurance_pension'
        WHEN 'pension' THEN 'insurance_pension'
        WHEN 'other' THEN 'other'
        ELSE 'other'
    END;

    NEW."Currency" := upper(btrim(NEW."Currency"));
    IF NEW."Currency" !~ '^[A-Z]{3}$' THEN
        RAISE EXCEPTION 'Asset currency must be a three-letter code.';
    END IF;
    IF NEW."CurrentValue" < 0 THEN
        RAISE EXCEPTION 'Asset CurrentValue cannot be negative.';
    END IF;
    IF NEW."ValuedAt" IS NULL THEN
        NEW."ValuedAt" := CURRENT_DATE;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS "TR_Assets_PrepareValuation" ON "Assets";
CREATE TRIGGER "TR_Assets_PrepareValuation"
BEFORE INSERT OR UPDATE OF "Kind", "CurrentValue", "Currency", "ValuedAt"
ON "Assets"
FOR EACH ROW EXECUTE FUNCTION fullworth_prepare_asset();

CREATE OR REPLACE FUNCTION fullworth_mirror_asset_valuation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    valuation_method text;
    actor_text text;
    actor_id uuid;
BEGIN
    IF current_setting('fullworth.asset_valuation_suppress', true) = 'on' THEN
        RETURN NEW;
    END IF;

    IF TG_OP = 'UPDATE'
       AND NEW."CurrentValue" IS NOT DISTINCT FROM OLD."CurrentValue"
       AND NEW."Currency" IS NOT DISTINCT FROM OLD."Currency"
       AND NEW."ValuedAt" IS NOT DISTINCT FROM OLD."ValuedAt" THEN
        RETURN NEW;
    END IF;

    valuation_method := lower(COALESCE(NULLIF(current_setting('fullworth.asset_valuation_method', true), ''), 'manual'));
    IF valuation_method NOT IN ('manual','purchase_price','internal_estimate','external_provider','appraisal','import','legacy') THEN
        valuation_method := 'manual';
    END IF;

    actor_text := NULLIF(current_setting('fullworth.asset_valuation_user_id', true), '');
    IF actor_text IS NOT NULL AND actor_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$' THEN
        actor_id := actor_text::uuid;
    ELSE
        actor_id := NULL;
    END IF;

    UPDATE "AssetValuations"
    SET "IsCurrent" = FALSE
    WHERE "AssetId" = NEW."Id" AND "IsCurrent" = TRUE;

    INSERT INTO "AssetValuations"
        ("Id", "FullWorthSpaceId", "AssetId", "Amount", "Currency", "ValuedAt", "Method",
         "IsCurrent", "IsAccepted", "CreatedByUserId", "CreatedAt")
    VALUES
        (gen_random_uuid(), NEW."FullWorthSpaceId", NEW."Id", NEW."CurrentValue", NEW."Currency", NEW."ValuedAt",
         valuation_method, TRUE, TRUE, actor_id, now());

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS "TR_Assets_MirrorValuation" ON "Assets";
CREATE TRIGGER "TR_Assets_MirrorValuation"
AFTER INSERT OR UPDATE OF "CurrentValue", "Currency", "ValuedAt"
ON "Assets"
FOR EACH ROW EXECUTE FUNCTION fullworth_mirror_asset_valuation();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS "TR_Assets_MirrorValuation" ON "Assets";
DROP FUNCTION IF EXISTS fullworth_mirror_asset_valuation();
DROP TRIGGER IF EXISTS "TR_Assets_PrepareValuation" ON "Assets";
DROP FUNCTION IF EXISTS fullworth_prepare_asset();
ALTER TABLE "Assets" DROP CONSTRAINT IF EXISTS "CK_Assets_Kind";

-- Restore the exact pre-migration free-form kind when the legacy valuation retained it.
UPDATE "Assets" a
SET "Kind" = v."InputSummaryJson"->>'legacyKind'
FROM "AssetValuations" v
WHERE v."AssetId" = a."Id"
  AND v."Method" = 'legacy'
  AND v."InputSummaryJson" ? 'legacyKind';

DROP TABLE IF EXISTS "AssetValuations";
""");
    }
}
