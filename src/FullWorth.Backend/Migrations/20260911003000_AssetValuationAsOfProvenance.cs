using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// An asset had one date doing two incompatible jobs. "ValuedAt" was supposed to say as of when the
/// value holds, but <c>fullworth_prepare_asset</c> stamped it with CURRENT_DATE whenever the row was
/// touched without one, and creating an asset mirrored that stamp into a "current" valuation. A stamp
/// that never described an appraisal was therefore indistinguishable from one — and it always looked
/// newer, so a legitimate appraisal dated last month appeared stale.
///
/// After this migration the two facts are separate:
/// <list type="bullet">
/// <item>"ValuedAt" is the date somebody stated — the owner, a document, a provider. NULL means nobody
/// ever said as of when, and nothing invents one any more.</item>
/// <item>"ValueRecordedAt" on the asset, and "CreatedAt" on the valuation, say when FullWorth learned
/// the figure. That is the row's own last-touched timestamp, not a statement about the world.</item>
/// <item>"ValuedAtIsStated" on the valuation marks which of the two its "ValuedAt" is: TRUE only when a
/// date was named. When FALSE, "ValuedAt" is the recording day, kept so history still sorts, and it
/// asserts nothing.</item>
/// </list>
/// No amount and no currency is touched anywhere in this migration.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260911003000_AssetValuationAsOfProvenance")]
public sealed class AssetValuationAsOfProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Assets"
  ADD COLUMN IF NOT EXISTS "ValueRecordedAt" timestamp with time zone NULL;
ALTER TABLE "AssetValuations"
  ADD COLUMN IF NOT EXISTS "ValuedAtIsStated" boolean NOT NULL DEFAULT FALSE;

-- When the current value was recorded. No column ever held this, so existing rows get the closest
-- thing the schema kept: the asset's own last-touched timestamp. It is a proxy, not a measurement - a
-- later rename bumped it too - which is exactly why it is maintained by the trigger from here on, and
-- only when the value itself changes. Updating this column alone fires neither trigger (both are
-- UPDATE OF "Kind"/"CurrentValue"/"Currency"/"ValuedAt"), so no history is written.
UPDATE "Assets"
SET "ValueRecordedAt" = COALESCE("UpdatedAt", "CreatedAt", now())
WHERE "ValueRecordedAt" IS NULL;

ALTER TABLE "Assets" ALTER COLUMN "ValueRecordedAt" SET DEFAULT now();
ALTER TABLE "Assets" ALTER COLUMN "ValueRecordedAt" SET NOT NULL;

-- Which existing valuation dates were actually stated? The only date this system ever invented is the
-- day the row was written, so a "ValuedAt" more than a day away from "CreatedAt" cannot be a stamp -
-- somebody named it. Everything within that day stays FALSE, including dates a user really did type:
-- a stated date equal to the recording day says nothing "CreatedAt" does not already say, while
-- promoting a synthetic CURRENT_DATE stamp to an appraisal date is the one error that must not happen
-- (it is what made real appraisals look stale). The day of slack absorbs the session time zone the
-- original CURRENT_DATE was evaluated in.
UPDATE "AssetValuations"
SET "ValuedAtIsStated" = TRUE
WHERE "Method" <> 'legacy'
  AND abs("ValuedAt" - ("CreatedAt")::date) > 1;

-- 'legacy' rows need the same question asked differently: their "CreatedAt" is the moment the earlier
-- backfill ran, and their date came out of COALESCE(Assets."ValuedAt", "UpdatedAt"::date,
-- "CreatedAt"::date, CURRENT_DATE). So compare against the asset instead - a date more than a day away
-- from the asset's last-touched day is neither that fallback nor a stamp, so it was stated.
UPDATE "AssetValuations" v
SET "ValuedAtIsStated" = TRUE
FROM "Assets" a
WHERE v."AssetId" = a."Id"
  AND v."Method" = 'legacy'
  AND a."ValuedAt" IS NOT NULL
  AND v."ValuedAt" = a."ValuedAt"
  AND abs(a."ValuedAt" - (COALESCE(a."UpdatedAt", a."CreatedAt"))::date) > 1;

-- The prepare trigger stops inventing an appraisal date and starts maintaining the recording
-- timestamp, which is the only one of the two facts it was ever in a position to know.
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

    -- "ValuedAt" is left exactly as it arrived. NULL stays NULL: nobody stated a date, and inventing
    -- CURRENT_DATE here is what made a stamp indistinguishable from an appraisal.
    IF TG_OP = 'INSERT' THEN
        NEW."ValueRecordedAt" := COALESCE(NEW."ValueRecordedAt", now());
    ELSIF NEW."CurrentValue" IS DISTINCT FROM OLD."CurrentValue"
       OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
       OR NEW."ValuedAt" IS DISTINCT FROM OLD."ValuedAt" THEN
        NEW."ValueRecordedAt" := now();
    ELSE
        NEW."ValueRecordedAt" := OLD."ValueRecordedAt";
    END IF;
    RETURN NEW;
END;
$$;

-- The mirror keeps writing a sortable "ValuedAt", but it now says whether that date was stated or is
-- just the day it wrote the row.
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
        ("Id", "FullWorthSpaceId", "AssetId", "Amount", "Currency", "ValuedAt", "ValuedAtIsStated", "Method",
         "IsCurrent", "IsAccepted", "CreatedByUserId", "CreatedAt")
    VALUES
        (gen_random_uuid(), NEW."FullWorthSpaceId", NEW."Id", NEW."CurrentValue", NEW."Currency",
         COALESCE(NEW."ValuedAt", CURRENT_DATE), NEW."ValuedAt" IS NOT NULL,
         valuation_method, TRUE, TRUE, actor_id, now());

    RETURN NEW;
END;
$$;

-- Last, the asset's own cached date. A "ValuedAt" indistinguishable from the row's last-touched day is
-- a stamp: it is dropped rather than kept as a pseudo-appraisal, and nothing is lost because
-- "ValueRecordedAt" now carries that same day honestly. A date further away was stated and stays.
-- Both triggers are off for this one statement on purpose: the new prepare trigger would otherwise
-- re-stamp "ValueRecordedAt" with the migration time, and the mirror would file a fresh "current"
-- valuation for every asset - the migration would rewrite the very history it is trying to explain.
ALTER TABLE "Assets" DISABLE TRIGGER "TR_Assets_PrepareValuation";
ALTER TABLE "Assets" DISABLE TRIGGER "TR_Assets_MirrorValuation";

UPDATE "Assets"
SET "ValuedAt" = NULL
WHERE "ValuedAt" IS NOT NULL
  AND abs("ValuedAt" - (COALESCE("UpdatedAt", "CreatedAt"))::date) <= 1;

ALTER TABLE "Assets" ENABLE TRIGGER "TR_Assets_PrepareValuation";
ALTER TABLE "Assets" ENABLE TRIGGER "TR_Assets_MirrorValuation";
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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

-- Going back means going back to a schema with no way to say "unknown", so an unstated asset gets the
-- stamp again - from its recording day, which is what the stamp always was. No history is rewritten.
ALTER TABLE "Assets" DISABLE TRIGGER "TR_Assets_MirrorValuation";
UPDATE "Assets" SET "ValuedAt" = ("ValueRecordedAt")::date WHERE "ValuedAt" IS NULL;
ALTER TABLE "Assets" ENABLE TRIGGER "TR_Assets_MirrorValuation";

ALTER TABLE "AssetValuations" DROP COLUMN IF EXISTS "ValuedAtIsStated";
ALTER TABLE "Assets" DROP COLUMN IF EXISTS "ValueRecordedAt";
""");
    }
}
