using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Die Tabelle hinter <c>GET /api/bank-capabilities</c> faellt weg (#177).
///
/// Drei tote Dinge in einer Reihe, und das dritte machte das erste gefaehrlich:
///
/// <list type="number">
///   <item>Die Route hatte keinen Aufrufer.</item>
///   <item>In <c>BankValidationRecords</c> hat NIE etwas geschrieben - kein Dienst, kein Import, kein
///         Seeder. Die Tabelle war in jeder Instanz leer, ein Drop kann hier nichts verlieren.</item>
///   <item>Genau fuer diesen Fall hatte der Handler einen Rueckfall: eine fest eingebaute Liste
///         "geplanter, noch nicht gepruefter" Institute - DKB, ING, PayPal, C24, Revolut. Das war die
///         einzige Antwort, die der Endpunkt je gegeben hat, und sie ist inzwischen falsch: ING
///         laeuft ueber den eigenen FinTS-Weg. Eine Oberflaeche dafuer haette also nicht eine Luecke
///         gefuellt, sondern eine Unwahrheit angezeigt.</item>
/// </list>
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260924140000_DropBankValidationRecords")]
public sealed class DropBankValidationRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE IF EXISTS "BankValidationRecords";
        """);

    /// <summary>Die Struktur zurueck, nicht die Daten - es gab nie welche.</summary>
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE IF NOT EXISTS "BankValidationRecords" (
          "Id" uuid PRIMARY KEY,
          "InstitutionKey" varchar(160) NOT NULL,
          "Provider" varchar(80) NOT NULL,
          "DisplayName" varchar(160) NOT NULL,
          "Country" varchar(2) NOT NULL,
          "IconAssetKey" varchar(160) NULL,
          "BalancesTested" boolean NOT NULL DEFAULT false,
          "TransactionsTested" boolean NOT NULL DEFAULT false,
          "PendingTested" boolean NOT NULL DEFAULT false,
          "MultiCurrencyTested" boolean NOT NULL DEFAULT false,
          "HistoryDepthDays" integer NULL,
          "LastValidatedAt" timestamptz NULL,
          "LastValidatedVersion" varchar(80) NULL,
          "KnownLimitations" varchar(2000) NULL,
          UNIQUE ("Provider","InstitutionKey")
        );
        """);
}
