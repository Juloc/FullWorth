using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Der Name, den die Bank dem Konto gibt - getrennt von dem, den der Benutzer ihm gibt (#125).
///
/// Bisher gab es nur <c>DisplayName</c> fuer beides, und beim Sync entschied eine RATEREGEL, ob der
/// vorhandene Name ueberschrieben werden darf: <c>LooksProviderGeneratedDisplayName</c> erkennt nur
/// GROSS_MIT_UNTERSTRICH. Damit wurde „DE123_GIRO" ueberschrieben und „Girokonto Gemeinschaft" nie -
/// obwohl beides vom Anbieter kommen kann. Ein eigener Name war nie sicher vor dem naechsten Sync,
/// und der Originalname war nach der ersten Umbenennung fuer immer weg.
///
/// Mit zwei Spalten braucht es keine Rateregel mehr: der Sync schreibt IMMER in
/// <c>ProviderDisplayName</c> und fasst <c>DisplayName</c> nur an, wenn der Benutzer ihn nie geaendert
/// hat - erkennbar daran, dass er dem bisherigen Anbieternamen entspricht.
///
/// Bestandskonten bekommen NICHTS nachgetragen. Der heutige <c>DisplayName</c> als Anbietername waere
/// eine Behauptung, die fuer jedes umbenannte Konto falsch ist - und sie wuerde genau die Namen zum
/// Ueberschreiben freigeben, die der Benutzer selbst vergeben hat. NULL heisst: der Anbietername ist
/// unbekannt, also bleibt der vorhandene Name stehen, bis der naechste Sync ihn nennt.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260915190000_AccountProviderDisplayName")]
public sealed class AccountProviderDisplayName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "Accounts" ADD COLUMN IF NOT EXISTS "ProviderDisplayName" character varying(200);
""");

    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""ALTER TABLE "Accounts" DROP COLUMN IF EXISTS "ProviderDisplayName";""");
}
