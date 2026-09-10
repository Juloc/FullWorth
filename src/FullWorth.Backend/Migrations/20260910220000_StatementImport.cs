using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// A statement file states a closing balance and the date it is valid for — the one thing a CSV export
/// almost never carries, and the difference between "here are some bookings" and an account whose value
/// is anchored. The upload parses it; the commit applies it. Between those two steps it has to be
/// stored, so the import job carries it.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260910220000_StatementImport")]
public sealed class StatementImport : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ImportJobs"
  ADD COLUMN IF NOT EXISTS "StatementBalance" numeric(20,8) NULL;
ALTER TABLE "ImportJobs"
  ADD COLUMN IF NOT EXISTS "StatementBalanceCurrency" character varying(3) NULL;
ALTER TABLE "ImportJobs"
  ADD COLUMN IF NOT EXISTS "StatementBalanceDate" date NULL;
ALTER TABLE "ImportJobs"
  ADD COLUMN IF NOT EXISTS "StatementAccount" character varying(300) NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ImportJobs" DROP COLUMN IF EXISTS "StatementAccount";
ALTER TABLE "ImportJobs" DROP COLUMN IF EXISTS "StatementBalanceDate";
ALTER TABLE "ImportJobs" DROP COLUMN IF EXISTS "StatementBalanceCurrency";
ALTER TABLE "ImportJobs" DROP COLUMN IF EXISTS "StatementBalance";
""");
    }
}
