using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Das Gedaechtnis fuer bestaetigte Umbuchungen (#146).
///
/// Bis hierher gab es keines. TransferDetectionService leitete bei jedem Lauf alles neu aus Betrag,
/// Drei-Tage-Fenster und Kontokennung ab; eine Bestaetigung des Benutzers hinterliess nichts. Solange
/// ein Fall in diese Mechanik passt, faellt das nicht auf - er wird jedes Mal wieder gefunden. Es
/// faellt dort auf, wo sie nicht greift: eine Gegenbuchung vier Tage spaeter, ein Betrag, der wegen
/// einer Gebuehr nicht exakt entgegengesetzt ist, oder gar keine Gegenbuchung, weil das Zielkonto
/// nicht in FullWorth gefuehrt wird.
///
/// "TargetAccountId" ist NULL fuer den externen Fall - das Sparkonto ausser Haus, das Bargeld, das
/// Verrechnungskonto. Kein Fremdschluessel-Zwang also, aber ein Fremdschluessel auf das Konto, auf
/// dem die Buchung steht: ohne dieses Konto ist die Regel sinnlos, und sie faellt mit ihm.
///
/// ON DELETE SET NULL fuer das Ziel ist Absicht und nicht Bequemlichkeit: wird das Gegenkonto
/// geloescht, bleibt die Erkenntnis "das ist eine Umbuchung" richtig - nur das Ziel ist dann eben
/// nicht mehr in FullWorth, und genau das bedeutet NULL.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260922160000_TransferRules")]
public sealed class TransferRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "TransferRules" (
  "Id" uuid NOT NULL,
  "FullWorthSpaceId" uuid NOT NULL,
  "AccountId" uuid NOT NULL,
  "NormalizedCounterparty" character varying(320) NOT NULL,
  "Direction" character varying(16) NOT NULL,
  "TargetAccountId" uuid NULL,
  "CreatedByUserId" uuid NOT NULL,
  "CreatedAt" timestamp with time zone NOT NULL,
  "UpdatedAt" timestamp with time zone NOT NULL,
  CONSTRAINT "PK_TransferRules" PRIMARY KEY ("Id"),
  CONSTRAINT "FK_TransferRules_Accounts_AccountId"
      FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE,
  CONSTRAINT "FK_TransferRules_Accounts_TargetAccountId"
      FOREIGN KEY ("TargetAccountId") REFERENCES "Accounts" ("Id") ON DELETE SET NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TransferRules_Space_Account_Counterparty_Direction"
  ON "TransferRules" ("FullWorthSpaceId", "AccountId", "NormalizedCounterparty", "Direction");
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "TransferRules";""");
}
