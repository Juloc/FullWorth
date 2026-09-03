using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// PurchaseDocument is the canonical receipt archive. ReceiptImagePath remains only as a backwards-
/// compatible pointer for older endpoints/UI and must therefore follow receipt-document mutations.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830215000_PurchaseReceiptDocumentMirror")]
public sealed class PurchaseReceiptDocumentMirror : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION fullworth_sync_purchase_receipt_mirror()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                purchase_id uuid;
                replacement text;
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    IF NEW."DocumentType" = 'receipt' THEN
                        UPDATE "Purchases"
                        SET "ReceiptImagePath" = NEW."StoragePath",
                            "UpdatedAt" = NOW()
                        WHERE "Id" = NEW."PurchaseId"
                          AND "ReceiptImagePath" IS NULL;
                    END IF;
                    RETURN NEW;
                END IF;

                IF TG_OP = 'UPDATE' THEN
                    -- If the document ceases to be a receipt, or its storage path changes, repair the
                    -- compatibility pointer only when it currently references that exact old document.
                    IF OLD."DocumentType" = 'receipt' AND
                       (NEW."DocumentType" <> 'receipt' OR OLD."StoragePath" IS DISTINCT FROM NEW."StoragePath") THEN
                        SELECT d."StoragePath" INTO replacement
                        FROM "PurchaseDocuments" d
                        WHERE d."PurchaseId" = OLD."PurchaseId"
                          AND d."DocumentType" = 'receipt'
                          AND d."Id" <> OLD."Id"
                        ORDER BY d."CreatedAt", d."Id"
                        LIMIT 1;

                        UPDATE "Purchases"
                        SET "ReceiptImagePath" = CASE
                                WHEN NEW."DocumentType" = 'receipt' THEN NEW."StoragePath"
                                ELSE replacement
                            END,
                            "UpdatedAt" = NOW()
                        WHERE "Id" = OLD."PurchaseId"
                          AND "ReceiptImagePath" = OLD."StoragePath";
                    ELSIF NEW."DocumentType" = 'receipt' THEN
                        UPDATE "Purchases"
                        SET "ReceiptImagePath" = NEW."StoragePath",
                            "UpdatedAt" = NOW()
                        WHERE "Id" = NEW."PurchaseId"
                          AND "ReceiptImagePath" IS NULL;
                    END IF;
                    RETURN NEW;
                END IF;

                purchase_id := OLD."PurchaseId";
                IF OLD."DocumentType" = 'receipt' THEN
                    SELECT d."StoragePath" INTO replacement
                    FROM "PurchaseDocuments" d
                    WHERE d."PurchaseId" = purchase_id
                      AND d."DocumentType" = 'receipt'
                    ORDER BY d."CreatedAt", d."Id"
                    LIMIT 1;

                    UPDATE "Purchases"
                    SET "ReceiptImagePath" = replacement,
                        "UpdatedAt" = NOW()
                    WHERE "Id" = purchase_id
                      AND "ReceiptImagePath" = OLD."StoragePath";
                END IF;
                RETURN OLD;
            END;
            $$;

            DROP TRIGGER IF EXISTS trg_purchase_receipt_document_mirror ON "PurchaseDocuments";
            CREATE TRIGGER trg_purchase_receipt_document_mirror
            AFTER INSERT OR UPDATE OF "DocumentType", "StoragePath" OR DELETE
            ON "PurchaseDocuments"
            FOR EACH ROW EXECUTE FUNCTION fullworth_sync_purchase_receipt_mirror();

            -- Repair only purchases which already have canonical receipt documents. Legacy purchases
            -- with just ReceiptImagePath are intentionally left intact until they are backfilled.
            -- PostgreSQL does not allow the UPDATE target ("p") to be referenced inside a FROM LATERAL
            -- item, so the canonical document is selected via a correlated scalar subquery instead.
            UPDATE "Purchases" p
            SET "ReceiptImagePath" = (
                    SELECT d."StoragePath"
                    FROM "PurchaseDocuments" d
                    WHERE d."PurchaseId" = p."Id" AND d."DocumentType" = 'receipt'
                    ORDER BY d."CreatedAt", d."Id"
                    LIMIT 1
                ),
                "UpdatedAt" = NOW()
            WHERE EXISTS (
                    SELECT 1 FROM "PurchaseDocuments" d
                    WHERE d."PurchaseId" = p."Id" AND d."DocumentType" = 'receipt'
               )
               AND (
                    p."ReceiptImagePath" IS NULL
                    OR NOT EXISTS (
                        SELECT 1 FROM "PurchaseDocuments" current_doc
                        WHERE current_doc."PurchaseId" = p."Id"
                          AND current_doc."DocumentType" = 'receipt'
                          AND current_doc."StoragePath" = p."ReceiptImagePath"
                    )
               );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS trg_purchase_receipt_document_mirror ON "PurchaseDocuments";
            DROP FUNCTION IF EXISTS fullworth_sync_purchase_receipt_mirror();
            """);
    }
}
