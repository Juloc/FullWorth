using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Der Institutionenkatalog wird lokal gehalten (#169).
///
/// Nach #165 kam der Gesundheitszustand der Banken aus der Datenbank, die Bankenliste selbst aber
/// weiterhin live von Enable Banking - der Bankdialog hing also immer noch an einem Fremdsystem,
/// diesmal an dem Teil, ohne den man gar keine Bank auswaehlen kann.
///
/// Fachliche Felder als Spalten, Protokollangaben des Anbieters als jsonb: Land, Name, Gruppe, Logo,
/// Beta und PSU-Typen sind das, wonach gesucht und sortiert wird; die Anmeldeverfahren sind eine
/// verschachtelte, vom Anbieter definierte Form, die die Oberflaeche nur durchreicht. Ein eigenes
/// Schema dafuer waere ein Nachbau fremder Protokollstruktur.
///
/// Stillgelegt statt geloescht: ein Institut, das der Anbieter nicht mehr meldet, wird inaktiv. Eine
/// bestehende Verbindung zeigt weiterhin auf diesen Namen, und ein Anbieter, der eine Bank fuer einen
/// Durchlauf vergisst, soll sie nicht aus der Geschichte tilgen.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260921110000_BankingInstitutionCatalog")]
public sealed class BankingInstitutionCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "BankingInstitutions" (
  "Id" uuid NOT NULL,
  "Country" character varying(2) NOT NULL,
  "Name" character varying(200) NOT NULL,
  "PsuTypesKey" character varying(120) NOT NULL,
  "PsuTypesJson" jsonb NOT NULL,
  "GroupJson" jsonb NULL,
  "LogoUrl" character varying(500) NULL,
  "Beta" boolean NOT NULL,
  "AuthMethodsJson" jsonb NOT NULL,
  "LastSeenAt" timestamp with time zone NOT NULL,
  "IsActive" boolean NOT NULL,
  "UpdatedAt" timestamp with time zone NOT NULL,
  CONSTRAINT "PK_BankingInstitutions" PRIMARY KEY ("Id")
);
""");
        // Die Identitaet eines Eintrags beim Anbieter: dieselbe Bank kommt als getrennter Privat- und
        // Geschaeftseintrag, und beide muessen nebeneinander stehen koennen. Ohne diesen Index legt
        // ein zweiter Durchlauf dieselbe Bank noch einmal an, statt sie zu aktualisieren.
        migrationBuilder.Sql("""
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankingInstitutions_Entry"
  ON "BankingInstitutions" ("Country", "Name", "PsuTypesKey");
""");
        // Der einzige Lesepfad, der zaehlt: die Bankauswahl fragt immer nach Land und will nur aktive.
        migrationBuilder.Sql("""
CREATE INDEX IF NOT EXISTS "IX_BankingInstitutions_CountryActive"
  ON "BankingInstitutions" ("Country", "IsActive");
""");

        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "BankingInstitutionRefreshes" (
  "Id" uuid NOT NULL,
  "Country" character varying(2) NOT NULL,
  "LastAttemptAt" timestamp with time zone NULL,
  "LastSuccessfulAt" timestamp with time zone NULL,
  "LastError" character varying(200) NULL,
  CONSTRAINT "PK_BankingInstitutionRefreshes" PRIMARY KEY ("Id")
);
""");
        migrationBuilder.Sql("""
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankingInstitutionRefreshes_Country"
  ON "BankingInstitutionRefreshes" ("Country");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Ein Zwischenspeicher: wegwerfen kostet nichts, der Hintergrunddienst baut ihn wieder auf.
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "BankingInstitutionRefreshes";""");
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "BankingInstitutions";""");
    }
}
