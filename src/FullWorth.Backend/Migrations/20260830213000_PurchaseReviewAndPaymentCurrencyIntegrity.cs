using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830213000_PurchaseReviewAndPaymentCurrencyIntegrity")]
public partial class PurchaseReviewAndPaymentCurrencyIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION fullworth_mark_purchase_needs_review(purchase_id uuid)
            RETURNS void
            LANGUAGE plpgsql
            AS $$
            BEGIN
                UPDATE "Purchases"
                SET "Status" = CASE WHEN "Status" = 'confirmed' THEN 'review' ELSE "Status" END,
                    "ReviewState" = CASE WHEN "ReviewState" = 'confirmed' THEN 'needs_review' ELSE "ReviewState" END,
                    "UpdatedAt" = NOW()
                WHERE "Id" = purchase_id;

                DELETE FROM "PurchaseDifferenceAcceptances"
                WHERE "PurchaseId" = purchase_id;
            END;
            $$;

            CREATE OR REPLACE FUNCTION fullworth_purchase_item_changed()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                purchase_id uuid;
            BEGIN
                IF TG_OP = 'UPDATE' AND NOT (
                    OLD."ProductId" IS DISTINCT FROM NEW."ProductId" OR
                    OLD."CategoryId" IS DISTINCT FROM NEW."CategoryId" OR
                    OLD."RawName" IS DISTINCT FROM NEW."RawName" OR
                    OLD."Name" IS DISTINCT FROM NEW."Name" OR
                    OLD."Brand" IS DISTINCT FROM NEW."Brand" OR
                    OLD."Sku" IS DISTINCT FROM NEW."Sku" OR
                    OLD."Barcode" IS DISTINCT FROM NEW."Barcode" OR
                    OLD."Asin" IS DISTINCT FROM NEW."Asin" OR
                    OLD."Quantity" IS DISTINCT FROM NEW."Quantity" OR
                    OLD."QuantityUnit" IS DISTINCT FROM NEW."QuantityUnit" OR
                    OLD."PackageQuantity" IS DISTINCT FROM NEW."PackageQuantity" OR
                    OLD."PackageUnit" IS DISTINCT FROM NEW."PackageUnit" OR
                    OLD."PackageCount" IS DISTINCT FROM NEW."PackageCount" OR
                    OLD."UnitPrice" IS DISTINCT FROM NEW."UnitPrice" OR
                    OLD."BaseUnitPrice" IS DISTINCT FROM NEW."BaseUnitPrice" OR
                    OLD."TotalPrice" IS DISTINCT FROM NEW."TotalPrice" OR
                    OLD."DiscountAmount" IS DISTINCT FROM NEW."DiscountAmount" OR
                    OLD."DepositAmount" IS DISTINCT FROM NEW."DepositAmount" OR
                    OLD."TaxRate" IS DISTINCT FROM NEW."TaxRate" OR
                    OLD."TaxAmount" IS DISTINCT FROM NEW."TaxAmount" OR
                    OLD."Currency" IS DISTINCT FROM NEW."Currency" OR
                    OLD."LineType" IS DISTINCT FROM NEW."LineType"
                ) THEN
                    RETURN NEW;
                END IF;

                IF TG_OP = 'DELETE' THEN purchase_id := OLD."PurchaseId";
                ELSE purchase_id := NEW."PurchaseId";
                END IF;

                PERFORM fullworth_mark_purchase_needs_review(purchase_id);
                IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                RETURN NEW;
            END;
            $$;

            CREATE OR REPLACE FUNCTION fullworth_purchase_payment_changed()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                purchase_id uuid;
            BEGIN
                IF TG_OP = 'UPDATE' AND NOT (
                    OLD."TransactionId" IS DISTINCT FROM NEW."TransactionId" OR
                    OLD."Amount" IS DISTINCT FROM NEW."Amount" OR
                    OLD."Currency" IS DISTINCT FROM NEW."Currency"
                ) THEN
                    RETURN NEW;
                END IF;

                IF TG_OP = 'DELETE' THEN purchase_id := OLD."PurchaseId";
                ELSE purchase_id := NEW."PurchaseId";
                END IF;

                PERFORM fullworth_mark_purchase_needs_review(purchase_id);
                IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                RETURN NEW;
            END;
            $$;

            DROP TRIGGER IF EXISTS trg_purchase_items_review ON "PurchaseItems";
            CREATE TRIGGER trg_purchase_items_review
            AFTER INSERT OR UPDATE OR DELETE ON "PurchaseItems"
            FOR EACH ROW EXECUTE FUNCTION fullworth_purchase_item_changed();

            DROP TRIGGER IF EXISTS trg_purchase_payments_review ON "PurchasePaymentLinks";
            CREATE TRIGGER trg_purchase_payments_review
            AFTER INSERT OR UPDATE OR DELETE ON "PurchasePaymentLinks"
            FOR EACH ROW EXECUTE FUNCTION fullworth_purchase_payment_changed();
            """);

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION fullworth_payment_link_transaction_currency()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                transaction_currency text;
            BEGIN
                SELECT "Currency" INTO transaction_currency
                FROM "Transactions"
                WHERE "Id" = NEW."TransactionId";

                IF transaction_currency IS NULL THEN
                    RAISE EXCEPTION 'Purchase payment link references an unknown transaction.';
                END IF;

                NEW."Currency" := transaction_currency;
                RETURN NEW;
            END;
            $$;

            DROP TRIGGER IF EXISTS trg_purchase_payment_currency ON "PurchasePaymentLinks";
            CREATE TRIGGER trg_purchase_payment_currency
            BEFORE INSERT OR UPDATE OF "TransactionId", "Currency" ON "PurchasePaymentLinks"
            FOR EACH ROW EXECUTE FUNCTION fullworth_payment_link_transaction_currency();

            UPDATE "PurchasePaymentLinks" link
            SET "Currency" = tx."Currency",
                "UpdatedAt" = NOW()
            FROM "Transactions" tx
            WHERE tx."Id" = link."TransactionId"
              AND link."Currency" IS DISTINCT FROM tx."Currency";
            """);

        // Preserve migrated data instead of silently truncating it. Old Amazon/legacy links can have
        // allocated the same bank charge to several purchases independently; flag those purchases for
        // explicit review and let the user decide how the real payment is divided.
        migrationBuilder.Sql("""
            WITH payment_totals AS (
                SELECT l."TransactionId", SUM(l."Amount") AS allocated_amount, ABS(MAX(t."Amount")) AS transaction_amount, MAX(t."Amount") AS signed_transaction_amount
                FROM "PurchasePaymentLinks" l
                JOIN "Transactions" t ON t."Id" = l."TransactionId"
                GROUP BY l."TransactionId"
            ), affected AS (
                SELECT DISTINCT l."PurchaseId"
                FROM "PurchasePaymentLinks" l
                JOIN payment_totals totals ON totals."TransactionId" = l."TransactionId"
                WHERE totals.allocated_amount > totals.transaction_amount + 0.00000001
                   OR totals.signed_transaction_amount >= 0
            )
            UPDATE "Purchases" p
            SET "Status" = CASE WHEN p."Status" = 'confirmed' THEN 'review' ELSE p."Status" END,
                "ReviewState" = CASE WHEN p."ReviewState" = 'confirmed' THEN 'needs_review' ELSE p."ReviewState" END,
                "UpdatedAt" = NOW()
            WHERE p."Id" IN (SELECT "PurchaseId" FROM affected);

            WITH payment_totals AS (
                SELECT l."TransactionId", SUM(l."Amount") AS allocated_amount, ABS(MAX(t."Amount")) AS transaction_amount, MAX(t."Amount") AS signed_transaction_amount
                FROM "PurchasePaymentLinks" l
                JOIN "Transactions" t ON t."Id" = l."TransactionId"
                GROUP BY l."TransactionId"
            ), affected AS (
                SELECT DISTINCT l."PurchaseId"
                FROM "PurchasePaymentLinks" l
                JOIN payment_totals totals ON totals."TransactionId" = l."TransactionId"
                WHERE totals.allocated_amount > totals.transaction_amount + 0.00000001
                   OR totals.signed_transaction_amount >= 0
            )
            DELETE FROM "PurchaseDifferenceAcceptances"
            WHERE "PurchaseId" IN (SELECT "PurchaseId" FROM affected);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS trg_purchase_payment_currency ON "PurchasePaymentLinks";
            DROP FUNCTION IF EXISTS fullworth_payment_link_transaction_currency();

            DROP TRIGGER IF EXISTS trg_purchase_payments_review ON "PurchasePaymentLinks";
            DROP TRIGGER IF EXISTS trg_purchase_items_review ON "PurchaseItems";
            DROP FUNCTION IF EXISTS fullworth_purchase_payment_changed();
            DROP FUNCTION IF EXISTS fullworth_purchase_item_changed();
            DROP FUNCTION IF EXISTS fullworth_mark_purchase_needs_review(uuid);
            """);
    }
}
