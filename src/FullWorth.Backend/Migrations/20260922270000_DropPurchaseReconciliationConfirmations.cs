using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Die Tabelle der zweiten Differenz-Bestaetigung faellt weg (#177).
///
/// Es gab zwei Wege, eine verbleibende Differenz zu akzeptieren: "PurchaseDifferenceAcceptances"
/// (getrennt nach Artikel- und Zahlungsdifferenz, an den Betrag gebunden) und diese hier (an einen
/// Fingerabdruck des Zustands gebunden). Geschrieben hat in diese Tabelle nur
/// <c>POST /api/purchase-review/{id}/confirm-difference</c>, und diese Route hatte keinen Aufrufer -
/// die Artikelwerkstatt benutzt seit jeher den anderen Weg. Die Tabelle war also in jeder Instanz
/// leer, und ein Drop kann hier nichts verlieren.
///
/// Ein Down gibt es bewusst nicht als Datenwiederherstellung, nur als Struktur: was nie geschrieben
/// wurde, ist auch nicht zurueckzuholen.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260922270000_DropPurchaseReconciliationConfirmations")]
public sealed class DropPurchaseReconciliationConfirmations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE IF EXISTS "PurchaseReconciliationConfirmations";
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE IF NOT EXISTS "PurchaseReconciliationConfirmations" (
          "PurchaseId" uuid PRIMARY KEY REFERENCES "Purchases"("Id") ON DELETE CASCADE,
          "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
          "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE RESTRICT,
          "ItemDifference" numeric(20,8) NOT NULL,
          "TransactionDifference" numeric(20,8) NULL,
          "StateFingerprint" text NOT NULL DEFAULT '',
          "ConfirmedAt" timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_PurchaseReconciliationConfirmations_Space"
          ON "PurchaseReconciliationConfirmations"("FullWorthSpaceId","ConfirmedAt" DESC);
        """);
}
