using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20261105120000_AddPaperlessUploads")]
public partial class AddPaperlessUploads : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Wie die Tabellen des Beleg-Imports bewusst SQL-/Store-gestuetzt und nicht Teil des EF-Modells.
        // Checksum ist der MD5 der abgelegten Datei - derselbe Wert, den Paperless als Pruefsumme des
        // Originals fuehrt. Er ist der einzige Weg, ein Dokument, das FullWorth selbst abgelegt hat,
        // beim spaeteren Import wiederzuerkennen: Paperless vergibt die Dokumentnummer erst nach der
        // Verarbeitung, die Ablage liefert nur eine Auftragsnummer.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "PaperlessUploads" (
              "Id" uuid NOT NULL,
              "FullWorthSpaceId" uuid NOT NULL,
              "PurchaseId" uuid NOT NULL,
              "Checksum" character varying(32) NOT NULL,
              "TaskId" character varying(64) NULL,
              "UploadedByUserId" uuid NOT NULL,
              "UploadedAt" timestamp with time zone NOT NULL,
              CONSTRAINT "PK_PaperlessUploads" PRIMARY KEY ("Id"),
              CONSTRAINT "FK_PaperlessUploads_Purchases_PurchaseId"
                FOREIGN KEY ("PurchaseId") REFERENCES "Purchases" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaperlessUploads_PurchaseId" ON "PaperlessUploads" ("PurchaseId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaperlessUploads_FullWorthSpaceId_Checksum" ON "PaperlessUploads" ("FullWorthSpaceId", "Checksum");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "PaperlessUploads";""");
}
