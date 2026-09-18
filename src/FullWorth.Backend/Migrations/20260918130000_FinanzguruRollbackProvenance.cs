using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Zwei Nachweise, die dem Finanzguru-Import bisher fehlten und seine eigene Ruecknahme blockierten
/// oder Spuren hinterliessen (#175).
///
/// "ImportJobCreatedAccounts" haelt fest, welche Konten EIN Import angelegt hat - Finanzguru kann in
/// einem Lauf mehrere zugleich anlegen (je ein Konto pro Quellkonto in der Datei), waehrend
/// "ImportJobs.CreatedAccountId" nur eines kennt. Die Ruecknahme prueft ab jetzt beides: die alte
/// Spalte fuer den bestehenden CSV-/Auszugsweg, diese Tabelle zusaetzlich fuer Finanzguru. Ein Konto
/// bleibt in jedem Fall stehen, solange etwas darin steckt - das entscheidet weiterhin
/// ImportJobStore.RollbackAsync, nicht diese Migration.
///
/// "TransactionAllocations.CreatedByImportJobId" unterscheidet eine Aufteilung, die der Import selbst
/// beim Anlegen der Buchung geschrieben hat (Teilbuchung/Restbetrag), von einer, die der Nutzer
/// hinterher von Hand angelegt hat. Nur die zweite ist Arbeit, die eine Ruecknahme schuetzen muss - die
/// erste ist das eigene Ergebnis des Imports und darf seine eigene Ruecknahme nicht blockieren. NULL
/// bleibt der Normalfall (von Hand, oder ein anderer Importer) und blockiert wie bisher.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260918130000_FinanzguruRollbackProvenance")]
public sealed class FinanzguruRollbackProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "ImportJobCreatedAccounts" (
  "ImportJobId" uuid NOT NULL,
  "AccountId" uuid NOT NULL,
  "CreatedAt" timestamp with time zone NOT NULL,
  CONSTRAINT "PK_ImportJobCreatedAccounts" PRIMARY KEY ("ImportJobId","AccountId"),
  CONSTRAINT "UQ_ImportJobCreatedAccounts_AccountId" UNIQUE ("AccountId"),
  CONSTRAINT "FK_ImportJobCreatedAccounts_ImportJobs_ImportJobId"
    FOREIGN KEY ("ImportJobId") REFERENCES "ImportJobs" ("Id") ON DELETE CASCADE,
  CONSTRAINT "FK_ImportJobCreatedAccounts_Accounts_AccountId"
    FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE
);

ALTER TABLE "TransactionAllocations"
  ADD COLUMN IF NOT EXISTS "CreatedByImportJobId" uuid NULL;

ALTER TABLE "TransactionAllocations"
  DROP CONSTRAINT IF EXISTS "FK_TransactionAllocations_ImportJobs_CreatedByImportJobId";
ALTER TABLE "TransactionAllocations"
  ADD CONSTRAINT "FK_TransactionAllocations_ImportJobs_CreatedByImportJobId"
  FOREIGN KEY ("CreatedByImportJobId") REFERENCES "ImportJobs" ("Id") ON DELETE SET NULL;
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "TransactionAllocations"
  DROP CONSTRAINT IF EXISTS "FK_TransactionAllocations_ImportJobs_CreatedByImportJobId";
ALTER TABLE "TransactionAllocations"
  DROP COLUMN IF EXISTS "CreatedByImportJobId";

DROP TABLE IF EXISTS "ImportJobCreatedAccounts";
""");
}
