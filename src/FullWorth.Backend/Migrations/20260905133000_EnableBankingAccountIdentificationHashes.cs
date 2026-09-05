using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260905133000_EnableBankingAccountIdentificationHashes")]
public sealed class EnableBankingAccountIdentificationHashes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "IdentificationHashesJson",
            table: "Accounts",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        // Seed every existing account's current primary hash as an alias. This makes upgrades safe
        // before the next provider sync supplies the full identification_hashes array.
        migrationBuilder.Sql("""
            UPDATE "Accounts"
            SET "IdentificationHashesJson" = jsonb_build_array("IdentificationHash")
            WHERE "IdentificationHash" IS NOT NULL
              AND length("IdentificationHash") > 0
              AND ("IdentificationHashesJson" = '[]'::jsonb OR "IdentificationHashesJson" IS NULL);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IdentificationHashesJson",
            table: "Accounts");
    }
}
