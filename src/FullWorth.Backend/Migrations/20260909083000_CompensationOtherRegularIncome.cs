using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Compensation;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Table for "sonstige regelmäßige Einkünfte" (other regular income such as a Halbwaisenrente).
///
/// Like the other compensation tables (compensation_profiles, compensation_scenarios,
/// compensation_history) this is raw SQL rather than an EF entity, so it stays out of the EF model
/// snapshot and <c>HasPendingModelChanges()</c> remains false. The DDL is shared with
/// <see cref="CompensationOtherIncomeStore.CreateTableSql"/> and is idempotent, so it does not matter
/// whether the migration or the store created the table first.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260909083000_CompensationOtherRegularIncome")]
public sealed class CompensationOtherRegularIncome : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(CompensationOtherIncomeStore.CreateTableSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql($"DROP TABLE IF EXISTS {CompensationOtherIncomeStore.TableName};");
}
