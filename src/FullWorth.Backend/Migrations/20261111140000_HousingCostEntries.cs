using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20261111140000_HousingCostEntries")]
public partial class HousingCostEntries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Wie die Abrechnungen und Vertragsstaende SQL-/Store-gestuetzt und nicht Teil des EF-Modells. Eine Zeile
        // ist ein Betrag aus einer Heizkosten-, Nebenkosten- oder Hausgeldabrechnung fuer einen Zeitraum - der
        // Zeitraum steht dabei, weil Abrechnungen oft nur einen Teil des Jahres abdecken und ein Betrag ohne ihn
        // nicht mit Preisen zu vergleichen ist. Gehoert dem Benutzer (UserId), nicht dem Space.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "HousingCostEntries" (
              "Id" uuid NOT NULL,
              "FullWorthSpaceId" uuid NOT NULL,
              "UserId" uuid NOT NULL,
              "Kind" character varying(20) NOT NULL,
              "PeriodFrom" date NOT NULL,
              "PeriodTo" date NOT NULL,
              "Amount" numeric(12,2) NOT NULL,
              "Note" character varying(500) NULL,
              "PaperlessDocumentId" integer NULL,
              "CreatedAt" timestamp with time zone NOT NULL,
              "UpdatedAt" timestamp with time zone NOT NULL,
              CONSTRAINT "PK_HousingCostEntries" PRIMARY KEY ("Id"),
              CONSTRAINT "FK_HousingCostEntries_FullWorthSpaces_FullWorthSpaceId"
                FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE CASCADE,
              CONSTRAINT "FK_HousingCostEntries_Users_UserId"
                FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS "IX_HousingCostEntries_Owner"
              ON "HousingCostEntries" ("UserId", "FullWorthSpaceId", "PeriodFrom");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "HousingCostEntries";""");
}
