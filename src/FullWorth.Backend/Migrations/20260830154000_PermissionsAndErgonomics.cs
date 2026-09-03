using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830154000_PermissionsAndErgonomics")]
public partial class PermissionsAndErgonomics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "FinanceMemberRoleTemplates" (
  "FullWorthSpaceId" uuid NOT NULL REFERENCES "FullWorthSpaces"("Id") ON DELETE CASCADE,
  "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
  "Template" varchar(16) NOT NULL DEFAULT 'viewer',
  "UpdatedAt" timestamptz NOT NULL,
  PRIMARY KEY ("FullWorthSpaceId","UserId"),
  CONSTRAINT "CK_FinanceMemberRoleTemplates_Template" CHECK ("Template" IN ('owner','editor','viewer'))
);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"FinanceMemberRoleTemplates\";");
    }
}
