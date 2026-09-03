using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260901211500_BulkReceiptImports")]
public sealed class BulkReceiptImports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "ReceiptImportBatches" (
                "Id" uuid NOT NULL,
                "FullWorthSpaceId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                "SourceType" character varying(32) NOT NULL,
                "SourceName" character varying(200) NOT NULL,
                "Currency" character varying(3) NOT NULL,
                "Status" character varying(32) NOT NULL,
                "AutoStart" boolean NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "CompletedAt" timestamp with time zone NULL,
                CONSTRAINT "PK_ReceiptImportBatches" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_ReceiptImportBatches_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
                CONSTRAINT "FK_ReceiptImportBatches_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_ReceiptImportBatches_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            CREATE INDEX "IX_ReceiptImportBatches_FullWorthSpaceId_UserId_CreatedAt" ON "ReceiptImportBatches" ("FullWorthSpaceId", "UserId", "CreatedAt");

            CREATE TABLE "ReceiptImportItems" (
                "Id" uuid NOT NULL,
                "BatchId" uuid NOT NULL,
                "FullWorthSpaceId" uuid NOT NULL,
                "SourceType" character varying(32) NOT NULL,
                "ExternalKey" character varying(500) NOT NULL,
                "DisplayName" character varying(500) NOT NULL,
                "SourceReference" character varying(1000) NULL,
                "ContentFingerprint" character varying(64) NULL,
                "ReceiptScanJobId" uuid NULL,
                "PurchaseId" uuid NULL,
                "Status" character varying(32) NOT NULL,
                "Error" character varying(1000) NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_ReceiptImportItems" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ReceiptImportItems_ReceiptImportBatches_BatchId" FOREIGN KEY ("BatchId") REFERENCES "ReceiptImportBatches" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ReceiptImportItems_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_ReceiptImportItems_BatchExternalKey" ON "ReceiptImportItems" ("BatchId", "ExternalKey");
            CREATE INDEX "IX_ReceiptImportItems_SourceIdentity" ON "ReceiptImportItems" ("FullWorthSpaceId", "SourceType", "ExternalKey");
            CREATE INDEX "IX_ReceiptImportItems_BatchId_Status" ON "ReceiptImportItems" ("BatchId", "Status");
            CREATE INDEX "IX_ReceiptImportItems_ReceiptScanJobId" ON "ReceiptImportItems" ("ReceiptScanJobId");
            CREATE INDEX "IX_ReceiptImportItems_ContentFingerprint" ON "ReceiptImportItems" ("FullWorthSpaceId", "ContentFingerprint");

            CREATE TABLE "PaperlessReceiptConnections" (
                "FullWorthSpaceId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                "BaseUrl" character varying(1000) NOT NULL,
                "ApiTokenProtected" text NOT NULL,
                "DefaultQuery" character varying(1000) NULL,
                "IsEnabled" boolean NOT NULL,
                "LastSyncAt" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_PaperlessReceiptConnections" PRIMARY KEY ("FullWorthSpaceId"),
                CONSTRAINT "FK_PaperlessReceiptConnections_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PaperlessReceiptConnections_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "PaperlessReceiptConnections";
            DROP TABLE IF EXISTS "ReceiptImportItems";
            DROP TABLE IF EXISTS "ReceiptImportBatches";
            """);
    }
}
