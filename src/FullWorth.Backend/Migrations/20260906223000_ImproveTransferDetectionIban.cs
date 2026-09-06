using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260906223000_ImproveTransferDetectionIban")]
public sealed class ImproveTransferDetectionIban : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Accounts"
  ADD COLUMN IF NOT EXISTS "IbanLookup" character varying(128) NULL;

ALTER TABLE "Transactions"
  ADD COLUMN IF NOT EXISTS "CounterpartyAccountLookup" character varying(128) NULL;

CREATE INDEX IF NOT EXISTS "IX_Accounts_FullWorthSpaceId_IbanLookup"
  ON "Accounts" ("FullWorthSpaceId", "IbanLookup");

CREATE INDEX IF NOT EXISTS "IX_Transactions_CounterpartyAccountLookup"
  ON "Transactions" ("CounterpartyAccountLookup");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_Transactions_CounterpartyAccountLookup";
DROP INDEX IF EXISTS "IX_Accounts_FullWorthSpaceId_IbanLookup";

ALTER TABLE "Transactions"
  DROP COLUMN IF EXISTS "CounterpartyAccountLookup";

ALTER TABLE "Accounts"
  DROP COLUMN IF EXISTS "IbanLookup";
""");
    }
}
