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
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Transactions"
  DROP COLUMN IF EXISTS "UseForBalanceHistory";
""");
    }
}
