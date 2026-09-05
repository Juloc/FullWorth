using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260905140000_EnableBankingAccountMetadata")]
public sealed class EnableBankingAccountMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Usage",
            table: "Accounts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PsuStatus",
            table: "Accounts",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);
        migrationBuilder.AddColumn<decimal>(
            name: "CreditLimitAmount",
            table: "Accounts",
            type: "numeric(20,8)",
            precision: 20,
            scale: 8,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "CreditLimitCurrency",
            table: "Accounts",
            type: "character varying(3)",
            maxLength: 3,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Usage", table: "Accounts");
        migrationBuilder.DropColumn(name: "PsuStatus", table: "Accounts");
        migrationBuilder.DropColumn(name: "CreditLimitAmount", table: "Accounts");
        migrationBuilder.DropColumn(name: "CreditLimitCurrency", table: "Accounts");
    }
}
