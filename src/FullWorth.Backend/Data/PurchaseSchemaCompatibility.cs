using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Data;

/// <summary>
/// Bridges databases that already ran the parallel full-feature-parity purchase migrations before the
/// canonical Purchases/Articles migrations were merged. The operation is intentionally a pre-migration
/// rename only: no financial data is deleted or transformed here. The final integration migration copies
/// the preserved LegacyMain* data into the canonical schema and removes the temporary structures.
/// </summary>
public static class PurchaseSchemaCompatibility
{
    public static async Task PrepareBeforeMigrationsAsync(FullWorthDbContext db, CancellationToken ct)
    {
        // On a brand-new database (fresh install or an isolated per-test database) the database itself
        // does not exist yet; MigrateAsync will create it moments later. There is no legacy main-era
        // schema to preserve in that case, so skip the pre-migration rename step rather than failing to
        // open a connection to a database that has not been created yet.
        if (!await db.Database.CanConnectAsync(ct))
            return;

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
DO $$
DECLARE
    feature_applied boolean := false;
    main_discount_applied boolean := false;
    main_sources_applied boolean := false;
BEGIN
    IF to_regclass('public."__EFMigrationsHistory"') IS NULL THEN
        RETURN;
    END IF;

    EXECUTE 'SELECT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = ''20260830133000_PurchasesArticlesSystem'')'
        INTO feature_applied;
    IF feature_applied THEN
        RETURN;
    END IF;

    EXECUTE 'SELECT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = ''20260831061000_AddPurchaseDiscountDetails'')'
        INTO main_discount_applied;
    EXECUTE 'SELECT EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = ''20260831050000_AddReceiptScanSources'')'
        INTO main_sources_applied;

    -- FullFeatureParity used ProductAliases for a different, category-learning shape. Preserve it under
    -- an explicit legacy name before the canonical ProductAliases table is created.
    IF to_regclass('public."ProductAliases"') IS NOT NULL
       AND to_regclass('public."LegacyMainProductAliases"') IS NULL
       AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='ProductAliases' AND column_name='NormalizedName')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='ProductAliases' AND column_name='ProductId') THEN
        ALTER TABLE "ProductAliases" RENAME TO "LegacyMainProductAliases";
    END IF;

    IF main_sources_applied AND to_regclass('public."ReceiptScanSources"') IS NOT NULL
       AND to_regclass('public."LegacyMainReceiptScanSources"') IS NULL THEN
        ALTER TABLE "ReceiptScanSources" RENAME TO "LegacyMainReceiptScanSources";
        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='"LegacyMainReceiptScanSources"'::regclass AND conname='PK_ReceiptScanSources') THEN
            ALTER TABLE "LegacyMainReceiptScanSources" RENAME CONSTRAINT "PK_ReceiptScanSources" TO "PK_LegacyMainReceiptScanSources";
        END IF;
    END IF;

    IF main_discount_applied THEN
        -- Preserve the main-era structured discount table. Renaming the PK and indexes is required
        -- because PostgreSQL index names are schema-global even after the table itself is renamed.
        IF to_regclass('public."PurchaseDiscounts"') IS NOT NULL
           AND to_regclass('public."LegacyMainPurchaseDiscounts"') IS NULL THEN
            ALTER TABLE "PurchaseDiscounts" RENAME TO "LegacyMainPurchaseDiscounts";
            IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='"LegacyMainPurchaseDiscounts"'::regclass AND conname='PK_PurchaseDiscounts') THEN
                ALTER TABLE "LegacyMainPurchaseDiscounts" RENAME CONSTRAINT "PK_PurchaseDiscounts" TO "PK_LegacyMainPurchaseDiscounts";
            END IF;
            IF to_regclass('public."IX_PurchaseDiscounts_PurchaseId"') IS NOT NULL THEN
                ALTER INDEX "IX_PurchaseDiscounts_PurchaseId" RENAME TO "IX_LegacyMainPurchaseDiscounts_PurchaseId";
            END IF;
            IF to_regclass('public."IX_PurchaseDiscounts_PurchaseItemId"') IS NOT NULL THEN
                ALTER INDEX "IX_PurchaseDiscounts_PurchaseItemId" RENAME TO "IX_LegacyMainPurchaseDiscounts_PurchaseItemId";
            END IF;
            IF to_regclass('public."IX_PurchaseDiscounts_PurchaseId_Type"') IS NOT NULL THEN
                ALTER INDEX "IX_PurchaseDiscounts_PurchaseId_Type" RENAME TO "IX_LegacyMainPurchaseDiscounts_PurchaseId_Type";
            END IF;
        END IF;

        -- Rename only columns created by the parallel main discount migration. This makes the canonical
        -- AddColumn operations safe while retaining every value for the final copy migration.
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='SubtotalAmount')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_SubtotalAmount') THEN
            ALTER TABLE "Purchases" RENAME COLUMN "SubtotalAmount" TO "LegacyMain_SubtotalAmount";
        END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='DiscountAmount')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_DiscountAmount') THEN
            ALTER TABLE "Purchases" RENAME COLUMN "DiscountAmount" TO "LegacyMain_DiscountAmount";
        END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='DepositAmount')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_DepositAmount') THEN
            ALTER TABLE "Purchases" RENAME COLUMN "DepositAmount" TO "LegacyMain_DepositAmount";
        END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='TaxAmount')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_TaxAmount') THEN
            ALTER TABLE "Purchases" RENAME COLUMN "TaxAmount" TO "LegacyMain_TaxAmount";
        END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='RoundingAmount')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='Purchases' AND column_name='LegacyMain_RoundingAmount') THEN
            ALTER TABLE "Purchases" RENAME COLUMN "RoundingAmount" TO "LegacyMain_RoundingAmount";
        END IF;

        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='OriginalUnitPrice')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='LegacyMain_OriginalUnitPrice') THEN
            ALTER TABLE "PurchaseItems" RENAME COLUMN "OriginalUnitPrice" TO "LegacyMain_OriginalUnitPrice";
        END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='DiscountAmount')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='LegacyMain_DiscountAmount') THEN
            ALTER TABLE "PurchaseItems" RENAME COLUMN "DiscountAmount" TO "LegacyMain_DiscountAmount";
        END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='DiscountLabel')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='LegacyMain_DiscountLabel') THEN
            ALTER TABLE "PurchaseItems" RENAME COLUMN "DiscountLabel" TO "LegacyMain_DiscountLabel";
        END IF;
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='DepositAmount')
           AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='PurchaseItems' AND column_name='LegacyMain_DepositAmount') THEN
            ALTER TABLE "PurchaseItems" RENAME COLUMN "DepositAmount" TO "LegacyMain_DepositAmount";
        END IF;
    END IF;
END $$;
""";
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
