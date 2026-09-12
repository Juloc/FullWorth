using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Bank connectivity settings that belong to this installation get a home in the database.
///
/// The FinTS product id lived in the deploy stack's compose file as <c>FinTs__ProductId</c>, which is the
/// wrong place twice over: an operator had to edit YAML and restart the stack to change a value the app
/// could simply ask for, and the compose file ended up carrying something that says nothing about how
/// the containers are wired.
///
/// Enable Banking already worked the right way — a stored profile with its own application id — so this
/// closes a gap rather than inventing a pattern. The configured value stays as a fallback, so an
/// existing deployment keeps working until somebody types the id into Einstellungen.
///
/// One row, and the unique index says so: a second "instance" row would make which product id this
/// installation uses depend on insertion order.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260912130000_BankingInstanceSettings")]
public sealed class BankingInstanceSettingsTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "BankingInstanceSettings" (
  "Id" uuid NOT NULL,
  "ScopeKey" character varying(40) NOT NULL,
  "FinTsProductId" character varying(64) NOT NULL,
  "UpdatedAt" timestamp with time zone NOT NULL,
  CONSTRAINT "PK_BankingInstanceSettings" PRIMARY KEY ("Id")
);
""");
        migrationBuilder.Sql("""
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BankingInstanceSettings_ScopeKey"
  ON "BankingInstanceSettings" ("ScopeKey");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "BankingInstanceSettings";""");
    }
}
