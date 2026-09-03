using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260902132000_AddTaxAssistant")]
public sealed class AddTaxAssistant : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        TaxAssistantMigrationSchema.Up(migrationBuilder);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        TaxAssistantMigrationSchema.Down(migrationBuilder);
}
