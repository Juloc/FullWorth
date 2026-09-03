using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830214500_PurchasePaymentAllocationIntegrity")]
public sealed class PurchasePaymentAllocationIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION fullworth_purchase_payment_allocation_guard()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                transaction_amount numeric;
                transaction_currency text;
                transaction_space uuid;
                purchase_space uuid;
                already_allocated numeric;
                allocation_tolerance numeric;
            BEGIN
                IF NEW."Amount" <= 0 THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'Purchase payment allocation amount must be greater than zero.';
                END IF;

                -- Serialize allocation mutations for one bank transaction. Without this row lock two
                -- concurrent requests could both observe the same free amount and over-allocate it.
                SELECT ABS(tx."Amount"), tx."Currency", account."FullWorthSpaceId"
                INTO transaction_amount, transaction_currency, transaction_space
                FROM "Transactions" tx
                JOIN "Accounts" account ON account."Id" = tx."AccountId"
                WHERE tx."Id" = NEW."TransactionId"
                FOR UPDATE OF tx;

                IF transaction_amount IS NULL THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23503',
                        MESSAGE = 'Purchase payment link references an unknown transaction.';
                END IF;

                SELECT "FullWorthSpaceId" INTO purchase_space
                FROM "Purchases"
                WHERE "Id" = NEW."PurchaseId";

                IF purchase_space IS NULL THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23503',
                        MESSAGE = 'Purchase payment link references an unknown purchase.';
                END IF;

                IF NEW."FullWorthSpaceId" IS DISTINCT FROM purchase_space
                   OR NEW."FullWorthSpaceId" IS DISTINCT FROM transaction_space THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'Purchase payment link must stay inside one FullWorth Space.';
                END IF;

                -- Currency describes the ledger amount and is therefore derived from the transaction,
                -- never from receipt OCR or a client request.
                NEW."Currency" := transaction_currency;

                -- Mirror PurchaseArticleCalculator.Tolerance(currency): the application layer accepts a
                -- per-currency rounding tolerance when it validates allocations, so the database guard must
                -- accept the same slack. Without it the guard raises 23514 on legitimate allocations that
                -- differ from the transaction total only by sub-minor-unit rounding.
                allocation_tolerance := CASE
                    WHEN UPPER(transaction_currency) IN (
                        'BIF','CLP','DJF','GNF','ISK','JPY','KMF','KRW','PYG','RWF','UGX','VND','VUV','XAF','XOF','XPF')
                        THEN 0.5
                    WHEN UPPER(transaction_currency) IN ('BHD','IQD','JOD','KWD','LYD','OMR','TND')
                        THEN 0.0005
                    ELSE 0.005
                END;

                SELECT COALESCE(SUM(ABS(link."Amount")), 0)
                INTO already_allocated
                FROM "PurchasePaymentLinks" link
                WHERE link."TransactionId" = NEW."TransactionId"
                  AND link."Id" <> NEW."Id";

                IF ROUND(already_allocated + ABS(NEW."Amount"), 8) > ROUND(transaction_amount + allocation_tolerance, 8) THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'Purchase payment allocations exceed the linked transaction amount.',
                        DETAIL = format(
                            'transaction=%s amount=%s allocated=%s requested=%s',
                            NEW."TransactionId", transaction_amount, already_allocated, NEW."Amount");
                END IF;

                RETURN NEW;
            END;
            $$;

            DROP TRIGGER IF EXISTS trg_purchase_payment_allocation_guard ON "PurchasePaymentLinks";
            CREATE TRIGGER trg_purchase_payment_allocation_guard
            BEFORE INSERT OR UPDATE OF "TransactionId", "PurchaseId", "FullWorthSpaceId", "Amount", "Currency"
            ON "PurchasePaymentLinks"
            FOR EACH ROW EXECUTE FUNCTION fullworth_purchase_payment_allocation_guard();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS trg_purchase_payment_allocation_guard ON "PurchasePaymentLinks";
            DROP FUNCTION IF EXISTS fullworth_purchase_payment_allocation_guard();
            """);
    }
}
