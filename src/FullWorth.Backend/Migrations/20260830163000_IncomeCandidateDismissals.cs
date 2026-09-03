using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830163000_IncomeCandidateDismissals")]
public partial class IncomeCandidateDismissals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "IncomeCandidateDismissals" (
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "AccountId" uuid NOT NULL REFERENCES "Accounts"("Id") ON DELETE CASCADE,
  "NormalizedCounterparty" varchar(512) NOT NULL,
  "Currency" varchar(3) NOT NULL,
  "Cycle" varchar(24) NOT NULL,
  "DismissedAt" timestamptz NOT NULL,
  PRIMARY KEY ("FullWorthSpaceId","AccountId","NormalizedCounterparty","Currency","Cycle")
);
CREATE INDEX IF NOT EXISTS "IX_IncomeCandidateDismissals_AccountId"
  ON "IncomeCandidateDismissals"("AccountId");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"IncomeCandidateDismissals\";");
    }
}
