using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20261110110000_CompensationDocumentImports")]
public partial class CompensationDocumentImports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Wie die Tabellen des Beleg-Imports bewusst SQL-/Store-gestuetzt und nicht Teil des EF-Modells.
        //
        // Eine Zeile in CompensationDocumentImports ist ein Fund aus Paperless - eine Lohnabrechnung
        // ("payslip") oder ein Arbeitsvertrag bzw. Nachtrag ("contract"): erst "pending" (gefunden), dann
        // "needs_review" (PDF verschluesselt abgelegt, Werte ausgelesen), dann "confirmed" (der Benutzer hat
        // die Werte bestaetigt, ConfirmedId zeigt auf die Abrechnung bzw. den Vertragsstand) oder "dismissed".
        // Die Zeile bleibt nach Bestaetigung und Verwerfen stehen - sie ist die Erinnerung, dass dieses
        // Dokument schon einmal gesehen wurde. Gehoert dem Benutzer (UserId), nicht dem Space.
        //
        // EmploymentContractStates sind die bestaetigten Vertragsstaende: je Vertrag oder Nachtrag ein
        // Stand mit allen Werten ab "ValidFrom". Das Aenderungsprotokoll rechnet jeden Stand gegen den
        // vorherigen - gespeichert wird nur der Stand, nie der Unterschied.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "CompensationDocumentImports" (
              "Id" uuid NOT NULL,
              "FullWorthSpaceId" uuid NOT NULL,
              "UserId" uuid NOT NULL,
              "Kind" character varying(32) NOT NULL,
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
              "ConfirmedId" uuid NULL,
              "CreatedAt" timestamp with time zone NOT NULL,
              "UpdatedAt" timestamp with time zone NOT NULL,
              CONSTRAINT "PK_CompensationDocumentImports" PRIMARY KEY ("Id"),
              CONSTRAINT "FK_CompensationDocumentImports_FullWorthSpaces_FullWorthSpaceId"
                FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE,
              CONSTRAINT "FK_CompensationDocumentImports_Users_UserId"
                FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompensationDocumentImports_Document"
              ON "CompensationDocumentImports" ("UserId", "FullWorthSpaceId", "PaperlessSource", "PaperlessDocumentId");
            CREATE INDEX IF NOT EXISTS "IX_CompensationDocumentImports_Checksum"
              ON "CompensationDocumentImports" ("UserId", "FullWorthSpaceId", "Checksum");
            CREATE INDEX IF NOT EXISTS "IX_CompensationDocumentImports_Confirmed"
              ON "CompensationDocumentImports" ("ConfirmedId") WHERE "ConfirmedId" IS NOT NULL;

            CREATE TABLE IF NOT EXISTS "EmploymentContractStates" (
              "Id" uuid NOT NULL,
              "FullWorthSpaceId" uuid NOT NULL,
              "UserId" uuid NOT NULL,
              "ValidFrom" date NOT NULL,
              "Payload" jsonb NOT NULL,
              "CreatedAt" timestamp with time zone NOT NULL,
              "UpdatedAt" timestamp with time zone NOT NULL,
              CONSTRAINT "PK_EmploymentContractStates" PRIMARY KEY ("Id"),
              CONSTRAINT "FK_EmploymentContractStates_FullWorthSpaces_FullWorthSpaceId"
                FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE,
              CONSTRAINT "FK_EmploymentContractStates_Users_UserId"
                FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS "IX_EmploymentContractStates_Owner"
              ON "EmploymentContractStates" ("UserId", "FullWorthSpaceId", "ValidFrom");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "EmploymentContractStates";
            DROP TABLE IF EXISTS "CompensationDocumentImports";
            """);
}
