using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20261110110000_PayslipImports")]
public partial class PayslipImports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Wie die Tabellen des Beleg-Imports bewusst SQL-/Store-gestuetzt und nicht Teil des EF-Modells.
        // Eine Zeile ist ein Lohnabrechnungs-Fund aus Paperless: erst "pending" (gefunden), dann
        // "needs_review" (PDF verschluesselt abgelegt, Werte ausgelesen), dann "confirmed" (der Benutzer
        // hat die Werte bestaetigt, PayslipId zeigt auf compensation_payslips) oder "dismissed". Die
        // Zeile bleibt nach Bestaetigung und Verwerfen stehen - sie ist die Erinnerung, dass dieses
        // Dokument schon einmal gesehen wurde. Gehoert dem Benutzer (UserId), nicht dem Space.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "PayslipImports" (
              "Id" uuid NOT NULL,
              "FullWorthSpaceId" uuid NOT NULL,
              "UserId" uuid NOT NULL,
              "PaperlessSource" character varying(300) NOT NULL,
              "PaperlessDocumentId" integer NOT NULL,
              "PaperlessUrl" character varying(1000) NOT NULL,
              "Checksum" character varying(64) NULL,
              "Title" character varying(300) NOT NULL,
              "Status" character varying(20) NOT NULL,
              "Attempts" integer NOT NULL DEFAULT 0,
              "Error" character varying(1000) NULL,
              "Extraction" jsonb NULL,
              "FileName" character varying(255) NULL,
              "ContentType" character varying(100) NULL,
              "StoragePath" character varying(300) NULL,
              "EncryptionScheme" character varying(20) NULL,
              "PayslipId" uuid NULL,
              "CreatedAt" timestamp with time zone NOT NULL,
              "UpdatedAt" timestamp with time zone NOT NULL,
              CONSTRAINT "PK_PayslipImports" PRIMARY KEY ("Id"),
              CONSTRAINT "FK_PayslipImports_FullWorthSpaces_FullWorthSpaceId"
                FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE,
              CONSTRAINT "FK_PayslipImports_Users_UserId"
                FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PayslipImports_Document"
              ON "PayslipImports" ("UserId", "FullWorthSpaceId", "PaperlessSource", "PaperlessDocumentId");
            CREATE INDEX IF NOT EXISTS "IX_PayslipImports_Checksum"
              ON "PayslipImports" ("UserId", "FullWorthSpaceId", "Checksum");
            CREATE INDEX IF NOT EXISTS "IX_PayslipImports_Payslip"
              ON "PayslipImports" ("PayslipId") WHERE "PayslipId" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "PayslipImports";""");
}
