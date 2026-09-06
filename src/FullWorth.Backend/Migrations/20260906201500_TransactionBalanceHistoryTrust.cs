using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260906201500_TransactionBalanceHistoryTrust")]
public sealed class TransactionBalanceHistoryTrust : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Transactions"
  ADD COLUMN IF NOT EXISTS "UseForBalanceHistory" boolean NOT NULL DEFAULT TRUE;

UPDATE "Transactions"
SET "UseForBalanceHistory" = FALSE
WHERE "ExternalKey" LIKE 'finanzguru:%';

ALTER TABLE "Accounts"
  ADD COLUMN IF NOT EXISTS "ImportLinkedAccountId" uuid NULL;

CREATE INDEX IF NOT EXISTS "IX_Accounts_ImportLinkedAccountId"
  ON "Accounts" ("ImportLinkedAccountId");

ALTER TABLE "Accounts"
  DROP CONSTRAINT IF EXISTS "FK_Accounts_Accounts_ImportLinkedAccountId";
ALTER TABLE "Accounts"
  ADD CONSTRAINT "FK_Accounts_Accounts_ImportLinkedAccountId"
  FOREIGN KEY ("ImportLinkedAccountId") REFERENCES "Accounts" ("Id") ON DELETE SET NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Accounts"
  DROP CONSTRAINT IF EXISTS "FK_Accounts_Accounts_ImportLinkedAccountId";
DROP INDEX IF EXISTS "IX_Accounts_ImportLinkedAccountId";
ALTER TABLE "Accounts"
  DROP COLUMN IF EXISTS "ImportLinkedAccountId";

ALTER TABLE "Transactions"
  DROP COLUMN IF EXISTS "UseForBalanceHistory";
""");
    }
}
