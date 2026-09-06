using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260906231500_ContractMerges")]
public sealed class ContractMerges : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "MergedIntoContractId",
            table: "Contracts",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Contracts_MergedIntoContractId",
            table: "Contracts",
            column: "MergedIntoContractId");

        migrationBuilder.AddForeignKey(
            name: "FK_Contracts_Contracts_MergedIntoContractId",
            table: "Contracts",
            column: "MergedIntoContractId",
            principalTable: "Contracts",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Contracts_Contracts_MergedIntoContractId",
            table: "Contracts");

        migrationBuilder.DropIndex(
            name: "IX_Contracts_MergedIntoContractId",
            table: "Contracts");

        migrationBuilder.DropColumn(
            name: "MergedIntoContractId",
            table: "Contracts");
    }
}
