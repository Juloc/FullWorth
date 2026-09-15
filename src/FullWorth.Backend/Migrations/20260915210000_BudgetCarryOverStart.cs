using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Ab wann der Uebertrag gerechnet wird (#115).
///
/// Das ist eine andere Frage als "wann beginnt eine Periode", und bisher gab es nur die zweite. Ohne
/// die erste rechnet ein neu angelegtes Budget entweder ab der ersten Buchung ueberhaupt - was ein
/// Minus aus dem letzten Jahr in die heutige Periode traegt - oder gar nicht, was die Historie
/// wegwirft. Der Benutzer soll es entscheiden.
///
/// <c>CarryOverStart</c> ist "as-far-back-as-possible", "this-period" oder "from-date";
/// <c>CarryOverFrom</c> traegt das Datum, wenn er selbst eines nennt. Bestand bleibt NULL und wird als
/// "so weit zurueck wie moeglich" gelesen - das ist, was die vorhandene Rechnung heute tut.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260915210000_BudgetCarryOverStart")]
public sealed class BudgetCarryOverStart : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Budgets" ADD COLUMN IF NOT EXISTS "CarryOverStart" character varying(32);
""");
        migrationBuilder.Sql("""
ALTER TABLE "Budgets" ADD COLUMN IF NOT EXISTS "CarryOverFrom" date;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "Budgets" DROP COLUMN IF EXISTS "CarryOverFrom";""");
        migrationBuilder.Sql("""ALTER TABLE "Budgets" DROP COLUMN IF EXISTS "CarryOverStart";""");
    }
}
