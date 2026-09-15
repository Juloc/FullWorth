using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Was die Quelle ueber einen Beleg weiss, bleibt am Beleg stehen (#128).
///
/// Bisher kam aus Paperless einiges an: Dokumentdatum, Korrespondent, Schlagworte, Dateiname - und
/// gespeichert wurde davon der Name. Alles andere fiel beim Anlegen der Zeile auf den Boden. Wer
/// spaeter wissen wollte, von wann ein Beleg ist oder zu wem er gehoert, musste Paperless erneut
/// fragen, Dokument fuer Dokument, und genau solche Nachfragen sind der Grund fuer #127.
///
/// Der Text der Quelle (Paperless liefert seine OCR gleich in der Liste mit) steht ebenfalls hier.
/// Er ist kein zweiter Speicher fuer die Datei: die Datei bleibt bei Paperless, hier steht, was
/// FullWorth zum Zuordnen braucht.
///
/// <c>SourceModifiedAt</c> ist der Punkt, an dem eine Aenderung erkennbar wird - erst damit laesst
/// sich ein geaendertes Dokument gezielt nachziehen, statt alles neu zu importieren.
///
/// Die Tabelle wird mit rohem SQL gefuehrt und ist im EF-Modell nicht abgebildet; der Schnappschuss
/// braucht deshalb nichts davon zu wissen.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260915220000_ReceiptSourceMetadata")]
public sealed class ReceiptSourceMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ReceiptImportItems"
  ADD COLUMN IF NOT EXISTS "SourceDocumentDate" date,
  ADD COLUMN IF NOT EXISTS "SourceMimeType" character varying(128),
  ADD COLUMN IF NOT EXISTS "SourceCorrespondent" character varying(200),
  ADD COLUMN IF NOT EXISTS "SourceTagsJson" jsonb,
  ADD COLUMN IF NOT EXISTS "SourceText" text,
  ADD COLUMN IF NOT EXISTS "SourceModifiedAt" timestamp with time zone;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ReceiptImportItems"
  DROP COLUMN IF EXISTS "SourceModifiedAt",
  DROP COLUMN IF EXISTS "SourceText",
  DROP COLUMN IF EXISTS "SourceTagsJson",
  DROP COLUMN IF EXISTS "SourceCorrespondent",
  DROP COLUMN IF EXISTS "SourceMimeType",
  DROP COLUMN IF EXISTS "SourceDocumentDate";
""");
    }
}
