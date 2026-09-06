using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260906213000_PaperlessImportPresets")]
public sealed class PaperlessImportPresets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "PaperlessImportPresets" (
                "Id" uuid NOT NULL,
                "FullWorthSpaceId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                "Name" character varying(100) NOT NULL,
                "Query" character varying(4000) NULL,
                "EditorJson" text NULL,
                "AutoImport" boolean NOT NULL DEFAULT false,
                "AnalyzeAutomatically" boolean NOT NULL DEFAULT true,
                "Currency" character varying(3) NOT NULL DEFAULT 'EUR',
                "LastSeenDocumentId" integer NULL,
                "LastCheckedAt" timestamp with time zone NULL,
                "LastImportedAt" timestamp with time zone NULL,
                "LastError" character varying(1000) NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_PaperlessImportPresets" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_PaperlessImportPresets_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
                CONSTRAINT "FK_PaperlessImportPresets_FullWorthSpaces_FullWorthSpaceId"
                    FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PaperlessImportPresets_Users_UserId"
                    FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaperlessImportPresets_Space_Name"
                ON "PaperlessImportPresets" ("FullWorthSpaceId", lower("Name"));
            CREATE INDEX IF NOT EXISTS "IX_PaperlessImportPresets_AutoImport"
                ON "PaperlessImportPresets" ("AutoImport", "FullWorthSpaceId");
            CREATE INDEX IF NOT EXISTS "IX_ReceiptImportItems_PaperlessSourceReference"
                ON "ReceiptImportItems" ("FullWorthSpaceId", "SourceType", "SourceReference");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "PaperlessImportPresets";
            DROP INDEX IF EXISTS "IX_ReceiptImportItems_PaperlessSourceReference";
            """);
    }
}
