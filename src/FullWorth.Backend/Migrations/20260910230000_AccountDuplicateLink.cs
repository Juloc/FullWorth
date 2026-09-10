using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// An explicit, user-chosen and reversible "these two accounts are the same" link that does not depend
/// on an IBAN. ImportLinkedAccountId could not carry it: that column means "this import archive was
/// merged into that account" and the reconciliation service keeps MOVING bookings along it.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260910230000_AccountDuplicateLink")]
public sealed class AccountDuplicateLink : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Accounts"
  ADD COLUMN IF NOT EXISTS "DuplicateOfAccountId" uuid NULL;

ALTER TABLE "Accounts"
  ADD COLUMN IF NOT EXISTS "IncludeInNetWorthBeforeLink" boolean NULL;

CREATE INDEX IF NOT EXISTS "IX_Accounts_DuplicateOfAccountId"
  ON "Accounts" ("DuplicateOfAccountId");

ALTER TABLE "Accounts"
  DROP CONSTRAINT IF EXISTS "FK_Accounts_Accounts_DuplicateOfAccountId";
ALTER TABLE "Accounts"
  ADD CONSTRAINT "FK_Accounts_Accounts_DuplicateOfAccountId"
  FOREIGN KEY ("DuplicateOfAccountId") REFERENCES "Accounts" ("Id") ON DELETE SET NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Accounts"
  DROP CONSTRAINT IF EXISTS "FK_Accounts_Accounts_DuplicateOfAccountId";
DROP INDEX IF EXISTS "IX_Accounts_DuplicateOfAccountId";
ALTER TABLE "Accounts"
  DROP COLUMN IF EXISTS "IncludeInNetWorthBeforeLink";
ALTER TABLE "Accounts"
  DROP COLUMN IF EXISTS "DuplicateOfAccountId";
""");
    }
}
