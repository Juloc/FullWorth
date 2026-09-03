using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Final convergence migration for the two purchase/product implementations that existed in parallel.
/// It preserves already-entered main data, moves legacy product identities into the canonical Products
/// model and removes the obsolete compatibility tables/triggers. All operations are existence-guarded
/// so fresh, main-upgrade and feature-upgrade databases converge to the same runtime schema.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260831140000_UnifyLegacyPurchaseProductSchema")]
public sealed class UnifyLegacyPurchaseProductSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DO $$
BEGIN
    -- Copy amount metadata preserved by PurchaseSchemaCompatibility on an existing main database.
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_SubtotalAmount') THEN
        EXECUTE 'UPDATE "Purchases" SET "SubtotalAmount" = "LegacyMain_SubtotalAmount"';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_DiscountAmount') THEN
        EXECUTE 'UPDATE "Purchases" SET "DiscountAmount" = "LegacyMain_DiscountAmount"';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_DepositAmount') THEN
        EXECUTE 'UPDATE "Purchases" SET "DepositAmount" = "LegacyMain_DepositAmount"';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_TaxAmount') THEN
        EXECUTE 'UPDATE "Purchases" SET "TaxAmount" = "LegacyMain_TaxAmount"';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_RoundingAmount') THEN
        EXECUTE 'UPDATE "Purchases" SET "RoundingAmount" = COALESCE("LegacyMain_RoundingAmount", 0)';
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='LegacyMain_OriginalUnitPrice') THEN
        EXECUTE 'UPDATE "PurchaseItems" SET "OriginalUnitPrice" = "LegacyMain_OriginalUnitPrice"';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='LegacyMain_DiscountAmount') THEN
        EXECUTE 'UPDATE "PurchaseItems" SET "DiscountAmount" = "LegacyMain_DiscountAmount"';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='LegacyMain_DiscountLabel') THEN
        EXECUTE 'UPDATE "PurchaseItems" SET "DiscountLabel" = "LegacyMain_DiscountLabel"';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='LegacyMain_DepositAmount') THEN
        EXECUTE 'UPDATE "PurchaseItems" SET "DepositAmount" = "LegacyMain_DepositAmount"';
    END IF;

    -- Preserve structured discounts created by the parallel main implementation.
    IF to_regclass('public."LegacyMainPurchaseDiscounts"') IS NOT NULL THEN
        EXECUTE $copy$
            INSERT INTO "PurchaseDiscounts"
                ("Id","PurchaseId","PurchaseItemId","Type","Label","Amount","Percentage",
                 "CouponCode","RawText","Source","Confidence","CreatedAt","UpdatedAt")
            SELECT l."Id", l."PurchaseId", l."PurchaseItemId", l."Type", l."Label", GREATEST(l."Amount",0),
                   CASE WHEN l."Percentage" IS NULL THEN NULL
                        ELSE LEAST(100, GREATEST(0, l."Percentage"))::numeric(8,4) END,
                   l."CouponCode", l."RawText", l."Source", l."Confidence", l."CreatedAt", l."UpdatedAt"
            FROM "LegacyMainPurchaseDiscounts" l
            WHERE EXISTS (SELECT 1 FROM "Purchases" p WHERE p."Id"=l."PurchaseId")
              AND (l."PurchaseItemId" IS NULL OR EXISTS (SELECT 1 FROM "PurchaseItems" i WHERE i."Id"=l."PurchaseItemId"))
            ON CONFLICT ("Id") DO NOTHING
        $copy$;
    END IF;

    -- Main's earlier scan-source shape lacked PurchaseDocumentId/MimeType/UpdatedAt. Merge all ordered
    -- source rows into the canonical queue; when the canonical legacy backfill already occupies a sort
    -- slot, retain its stable Id and replace only source metadata.
    IF to_regclass('public."LegacyMainReceiptScanSources"') IS NOT NULL THEN
        EXECUTE $copy$
            INSERT INTO "ReceiptScanSources"
                ("Id","ReceiptScanJobId","PurchaseDocumentId","SortOrder","SourceType","OriginalFileName",
                 "MimeType","StoragePath","PageNumber","Fingerprint","SizeBytes","CreatedAt","UpdatedAt")
            SELECT l."Id", l."ReceiptScanJobId", NULL, l."SortOrder", l."SourceType", l."OriginalFileName",
                   l."ContentType", l."StoragePath", l."PageNumber", l."Fingerprint", l."SizeBytes", l."CreatedAt", l."CreatedAt"
            FROM "LegacyMainReceiptScanSources" l
            WHERE NOT EXISTS (SELECT 1 FROM "ReceiptScanSources" s WHERE s."Id"=l."Id")
            ON CONFLICT ("ReceiptScanJobId","SortOrder") DO UPDATE SET
                "SourceType"=EXCLUDED."SourceType",
                "OriginalFileName"=EXCLUDED."OriginalFileName",
                "MimeType"=EXCLUDED."MimeType",
                "StoragePath"=EXCLUDED."StoragePath",
                "PageNumber"=EXCLUDED."PageNumber",
                "Fingerprint"=EXCLUDED."Fingerprint",
                "SizeBytes"=EXCLUDED."SizeBytes",
                "UpdatedAt"=EXCLUDED."UpdatedAt"
        $copy$;
    END IF;

    -- Convert the main parity product identities to the single canonical Products model.
    IF to_regclass('public."ProductIdentities"') IS NOT NULL THEN
        EXECUTE $copy$
            INSERT INTO "Products"
                ("Id","FullWorthSpaceId","CanonicalName","Brand","DefaultCategoryId","DefaultQuantityUnit",
                 "DefaultPackageQuantity","DefaultPackageUnit","ImageReference","Notes","IsArchived","CreatedAt","UpdatedAt")
            SELECT p."Id", p."FullWorthSpaceId", p."CanonicalName", p."Brand", p."DefaultCategoryId",
                   'piece', p."UnitSize", p."UnitKind", NULL, NULL, false, p."CreatedAt", p."UpdatedAt"
            FROM "ProductIdentities" p
            ON CONFLICT ("Id") DO NOTHING
        $copy$;

        EXECUTE $copy$
            INSERT INTO "ProductBarcodes" ("Id","ProductId","Code","Standard","CreatedAt")
            SELECT gen_random_uuid(), p."Id", p."Barcode", 'unknown', p."CreatedAt"
            FROM "ProductIdentities" p
            WHERE p."Barcode" IS NOT NULL AND btrim(p."Barcode") <> ''
              AND EXISTS (SELECT 1 FROM "Products" cp WHERE cp."Id"=p."Id")
              AND NOT EXISTS (SELECT 1 FROM "ProductBarcodes" b WHERE b."ProductId"=p."Id" AND b."Code"=p."Barcode")
        $copy$;
    END IF;

    IF to_regclass('public."ProductIdentityAliases"') IS NOT NULL THEN
        EXECUTE $copy$
            INSERT INTO "ProductAliases"
                ("Id","ProductId","MerchantId","Alias","NormalizedAlias","AliasType","CreatedAt")
            SELECT a."Id", a."ProductIdentityId", NULL, a."NormalizedText", a."NormalizedText", 'legacy_learning', a."CreatedAt"
            FROM "ProductIdentityAliases" a
            WHERE EXISTS (SELECT 1 FROM "Products" p WHERE p."Id"=a."ProductIdentityId")
              AND NOT EXISTS (
                  SELECT 1 FROM "ProductAliases" ca
                  WHERE ca."ProductId"=a."ProductIdentityId" AND ca."MerchantId" IS NULL
                    AND ca."NormalizedAlias"=a."NormalizedText")
            ON CONFLICT ("Id") DO NOTHING
        $copy$;
    END IF;

    IF to_regclass('public."PurchaseItemProductLinks"') IS NOT NULL THEN
        EXECUTE $copy$
            UPDATE "PurchaseItems" i
            SET "ProductId" = l."ProductIdentityId", "UpdatedAt" = now()
            FROM "PurchaseItemProductLinks" l
            WHERE l."PurchaseItemId"=i."Id" AND i."ProductId" IS NULL
              AND EXISTS (SELECT 1 FROM "Products" p WHERE p."Id"=l."ProductIdentityId")
        $copy$;
    END IF;

    -- FullFeatureParity also had a lightweight ProductAliases category-learning table. Preserve each
    -- row as a canonical product+alias before dropping the obsolete table.
    IF to_regclass('public."LegacyMainProductAliases"') IS NOT NULL THEN
        EXECUTE $copy$
            INSERT INTO "Products"
                ("Id","FullWorthSpaceId","CanonicalName","Brand","DefaultCategoryId","DefaultQuantityUnit",
                 "DefaultPackageQuantity","DefaultPackageUnit","ImageReference","Notes","IsArchived","CreatedAt","UpdatedAt")
            SELECT l."Id", l."FullWorthSpaceId", l."DisplayName", NULL, l."CategoryId", 'piece', NULL, NULL,
                   NULL, NULL, false, l."CreatedAt", l."UpdatedAt"
            FROM "LegacyMainProductAliases" l
            ON CONFLICT ("Id") DO NOTHING
        $copy$;

        EXECUTE $copy$
            INSERT INTO "ProductAliases"
                ("Id","ProductId","MerchantId","Alias","NormalizedAlias","AliasType","CreatedAt")
            SELECT gen_random_uuid(), l."Id", NULL, l."DisplayName", l."NormalizedName", 'legacy_category', l."CreatedAt"
            FROM "LegacyMainProductAliases" l
            WHERE EXISTS (SELECT 1 FROM "Products" p WHERE p."Id"=l."Id")
              AND NOT EXISTS (
                  SELECT 1 FROM "ProductAliases" ca
                  WHERE ca."ProductId"=l."Id" AND ca."MerchantId" IS NULL
                    AND ca."NormalizedAlias"=l."NormalizedName")
        $copy$;
    END IF;
END $$;

-- Disable and remove the superseded legacy-product auto-link path.
DROP TRIGGER IF EXISTS "TR_PurchaseItems_ProductLink" ON "PurchaseItems";
DROP TRIGGER IF EXISTS "TR_PurchaseItems_ProductPrefill" ON "PurchaseItems";
DROP FUNCTION IF EXISTS fullworth_link_purchase_item_product();
DROP FUNCTION IF EXISTS fullworth_prefill_purchase_item_product();
DROP FUNCTION IF EXISTS fullworth_normalize_product_text(text);

DROP TABLE IF EXISTS "PurchaseItemProductLinks";
DROP TABLE IF EXISTS "ProductIdentityAliases";
DROP TABLE IF EXISTS "ProductIdentities";
DROP TABLE IF EXISTS "LegacyMainProductAliases";
DROP TABLE IF EXISTS "LegacyMainPurchaseDiscounts";
DROP TABLE IF EXISTS "LegacyMainReceiptScanSources";

ALTER TABLE "Purchases" DROP COLUMN IF EXISTS "LegacyMain_SubtotalAmount";
ALTER TABLE "Purchases" DROP COLUMN IF EXISTS "LegacyMain_DiscountAmount";
ALTER TABLE "Purchases" DROP COLUMN IF EXISTS "LegacyMain_DepositAmount";
ALTER TABLE "Purchases" DROP COLUMN IF EXISTS "LegacyMain_TaxAmount";
ALTER TABLE "Purchases" DROP COLUMN IF EXISTS "LegacyMain_RoundingAmount";
ALTER TABLE "PurchaseItems" DROP COLUMN IF EXISTS "LegacyMain_OriginalUnitPrice";
ALTER TABLE "PurchaseItems" DROP COLUMN IF EXISTS "LegacyMain_DiscountAmount";
ALTER TABLE "PurchaseItems" DROP COLUMN IF EXISTS "LegacyMain_DiscountLabel";
ALTER TABLE "PurchaseItems" DROP COLUMN IF EXISTS "LegacyMain_DepositAmount";
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deliberately irreversible: this migration removes a duplicate legacy data model after copying
        // all useful data into the canonical schema. Recreating two competing product systems on rollback
        // would be less safe than restoring from the normal database backup.
    }
}
