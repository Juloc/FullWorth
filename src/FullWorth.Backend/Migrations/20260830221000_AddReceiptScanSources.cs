using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830221000_AddReceiptScanSources")]
public partial class AddReceiptScanSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ReceiptScanJobs deliberately remains SQL/store-backed. The source table is likewise not an EF
        // aggregate: it is queue infrastructure and must not inflate the fullworth-domain model snapshot.
        migrationBuilder.Sql("""
            ALTER TABLE "ReceiptScanJobs"
              ADD COLUMN IF NOT EXISTS "WarningsJson" text NULL;

            CREATE TABLE IF NOT EXISTS "ReceiptScanSources" (
              "Id" uuid NOT NULL,
              "ReceiptScanJobId" uuid NOT NULL,
              "PurchaseDocumentId" uuid NULL,
              "SortOrder" integer NOT NULL,
              "SourceType" character varying(32) NOT NULL,
              "OriginalFileName" character varying(500) NOT NULL,
              "MimeType" character varying(100) NOT NULL,
              "StoragePath" character varying(1000) NOT NULL,
              "PageNumber" integer NULL,
              "Fingerprint" character varying(128) NOT NULL,
              "SizeBytes" bigint NOT NULL,
              "CreatedAt" timestamp with time zone NOT NULL,
              "UpdatedAt" timestamp with time zone NOT NULL,
              CONSTRAINT "PK_ReceiptScanSources" PRIMARY KEY ("Id"),
              CONSTRAINT "FK_ReceiptScanSources_ReceiptScanJobs_ReceiptScanJobId"
                FOREIGN KEY ("ReceiptScanJobId") REFERENCES "ReceiptScanJobs" ("Id") ON DELETE CASCADE,
              CONSTRAINT "FK_ReceiptScanSources_PurchaseDocuments_PurchaseDocumentId"
                FOREIGN KEY ("PurchaseDocumentId") REFERENCES "PurchaseDocuments" ("Id") ON DELETE SET NULL,
              CONSTRAINT "CK_ReceiptScanSources_SortOrder" CHECK ("SortOrder" >= 0),
              CONSTRAINT "CK_ReceiptScanSources_PageNumber" CHECK ("PageNumber" IS NULL OR "PageNumber" > 0),
              CONSTRAINT "CK_ReceiptScanSources_SourceType" CHECK ("SourceType" IN ('image', 'pdf_page'))
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReceiptScanSources_Job_SortOrder"
              ON "ReceiptScanSources" ("ReceiptScanJobId", "SortOrder");
            CREATE INDEX IF NOT EXISTS "IX_ReceiptScanSources_Job"
              ON "ReceiptScanSources" ("ReceiptScanJobId");
            CREATE INDEX IF NOT EXISTS "IX_ReceiptScanSources_Document"
              ON "ReceiptScanSources" ("PurchaseDocumentId");

            CREATE TABLE IF NOT EXISTS "ReceiptScanItemSources" (
              "PurchaseItemId" uuid NOT NULL,
              "ReceiptScanSourceId" uuid NOT NULL,
              "CreatedAt" timestamp with time zone NOT NULL,
              CONSTRAINT "PK_ReceiptScanItemSources" PRIMARY KEY ("PurchaseItemId", "ReceiptScanSourceId"),
              CONSTRAINT "FK_ReceiptScanItemSources_PurchaseItems_PurchaseItemId"
                FOREIGN KEY ("PurchaseItemId") REFERENCES "PurchaseItems" ("Id") ON DELETE CASCADE,
              CONSTRAINT "FK_ReceiptScanItemSources_ReceiptScanSources_ReceiptScanSourceId"
                FOREIGN KEY ("ReceiptScanSourceId") REFERENCES "ReceiptScanSources" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ReceiptScanItemSources_Source"
              ON "ReceiptScanItemSources" ("ReceiptScanSourceId");

            -- Existing single-file queue rows become one logical source. Legacy PDFs start with page 1;
            -- the processor expands them to all pages before the next extraction attempt.
            INSERT INTO "ReceiptScanSources"
                ("Id", "ReceiptScanJobId", "PurchaseDocumentId", "SortOrder", "SourceType",
                 "OriginalFileName", "MimeType", "StoragePath", "PageNumber", "Fingerprint",
                 "SizeBytes", "CreatedAt", "UpdatedAt")
            SELECT
                CAST(md5(j."Id"::text || ':legacy-source') AS uuid),
                j."Id",
                d."Id",
                0,
                CASE WHEN j."ContentType" = 'application/pdf' THEN 'pdf_page' ELSE 'image' END,
                j."FileName",
                j."ContentType",
                p."ReceiptImagePath",
                CASE WHEN j."ContentType" = 'application/pdf' THEN 1 ELSE NULL END,
                'legacy:' || j."Id"::text,
                COALESCE(d."SizeBytes", 0),
                j."CreatedAt",
                j."UpdatedAt"
            FROM "ReceiptScanJobs" j
            JOIN "Purchases" p ON p."Id" = j."PurchaseId"
            LEFT JOIN LATERAL (
                SELECT pd."Id", pd."SizeBytes"
                FROM "PurchaseDocuments" pd
                WHERE pd."PurchaseId" = j."PurchaseId"
                  AND pd."StoragePath" = p."ReceiptImagePath"
                ORDER BY pd."CreatedAt", pd."Id"
                LIMIT 1
            ) d ON TRUE
            WHERE p."ReceiptImagePath" IS NOT NULL
              AND NOT EXISTS (
                SELECT 1 FROM "ReceiptScanSources" s WHERE s."ReceiptScanJobId" = j."Id"
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "ReceiptScanItemSources";
            DROP TABLE IF EXISTS "ReceiptScanSources";
            ALTER TABLE "ReceiptScanJobs" DROP COLUMN IF EXISTS "WarningsJson";
            """);
    }
}
