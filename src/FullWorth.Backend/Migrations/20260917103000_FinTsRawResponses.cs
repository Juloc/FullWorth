using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Hebt auf, was die Bank geantwortet hat - damit niemand mehr raten muss, was sie geschickt hat.
///
/// Der Anlass ist eine Reihe von vier Fehlern hintereinander, und jeder kostete einen kompletten
/// Umlauf aus Vermutung, Release, Abruf und Logzeile: das Kontofeld in der falschen Version, eine
/// unerlaubte Ausgabewaehrung, ein Parser, der SUBSAFE verlangte, und zuletzt eine runde Stueckzahl,
/// die als "10," ankommt und an einem Regex scheiterte. Jedes Mal lagen die Daten vor - nur nicht
/// mehr, als die Frage aufkam. Der Parser liest acht Feldkennungen und verwirft den Rest; was er
/// nicht kennt, ist danach weg.
///
/// Warum das bis hierher nicht existierte, ist eine ueberdehnte Regel: eine FinTS-ANFRAGE traegt in
/// HNSHA die PIN, deshalb steht keine FinTS-Nachricht im Klartext im Log. Das gilt fuer LOGS. Die
/// ANTWORT HIWPD enthaelt Bestaende, keine PIN, und verschluesselt in der Datenbank ist etwas
/// anderes als offen im Containerlog.
///
/// Deshalb hier und nur hier:
///
/// <list type="bullet">
///   <item><c>Payload</c> ist FieldCipher-geschuetzt wie ProviderSessionId und AuthorizationId.</item>
///   <item>ON DELETE CASCADE: wer seine Bankverbindung trennt, wird auch das hier los.</item>
///   <item>Der Index traegt CapturedAt absteigend - gefragt wird immer nach der letzten Antwort.</item>
/// </list>
///
/// Aufbewahrt wird eine kurze Kette je Verbindung und Art, nicht die Geschichte: das hier ist ein
/// Werkzeug zum Nachsehen, kein Archiv.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260917103000_FinTsRawResponses")]
public sealed class FinTsRawResponses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "FinTsRawResponses" (
  "Id" uuid NOT NULL PRIMARY KEY,
  "BankConnectionId" uuid NOT NULL,
  "Kind" text NOT NULL,
  "Label" text NULL,
  "CapturedAt" timestamp with time zone NOT NULL,
  "PayloadLength" integer NOT NULL,
  "Payload" text NOT NULL,
  CONSTRAINT "FK_FinTsRawResponses_BankConnections_BankConnectionId"
    FOREIGN KEY ("BankConnectionId") REFERENCES "BankConnections" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_FinTsRawResponses_Connection_Kind_CapturedAt"
  ON "FinTsRawResponses" ("BankConnectionId", "Kind", "CapturedAt" DESC);
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DROP TABLE IF EXISTS "FinTsRawResponses";
""");
}
