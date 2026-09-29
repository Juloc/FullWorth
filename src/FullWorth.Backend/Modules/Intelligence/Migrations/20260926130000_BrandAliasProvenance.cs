using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Eine selbst gelernte Schreibweise sagt jetzt, woher sie kommt.
///
/// Der Grund ist die Leiter, die eine Instanz seit dem Wegfall der Cloud selbst abarbeitet:
/// mitgelieferter Katalog, dann Ableitung, dann - optional - Netz, dann KI. Die ersten Stufen
/// kosten nichts, die letzte kostet Tokens. Ohne diese Spalten sieht man einer Zeile nicht an,
/// welche sie geschrieben hat, und damit auch nicht, ob das Lernen tatsaechlich billiger wird.
///
/// Bestehende Zeilen bekommen <c>ai</c> und 0,55: sie stammen aus dem einzigen Weg, den es vorher
/// gab, und das ist die richtige Vorgabe - keine von ihnen wurde gerechnet.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260926130000_BrandAliasProvenance")]
public sealed class BrandAliasProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ResearchedBrandAliases" ADD COLUMN IF NOT EXISTS "Source" character varying(20) NOT NULL DEFAULT 'ai';
ALTER TABLE "ResearchedBrandAliases" ADD COLUMN IF NOT EXISTS "Confidence" numeric(6,5) NOT NULL DEFAULT 0.55;
ALTER TABLE "ResearchedBrandAliases" ADD COLUMN IF NOT EXISTS "AliasKind" character varying(10) NOT NULL DEFAULT 'exact';
ALTER TABLE "ResearchedBrandAliases" ADD COLUMN IF NOT EXISTS "Status" character varying(10) NOT NULL DEFAULT 'active';
ALTER TABLE "ResearchedBrandAliases" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamp with time zone NOT NULL DEFAULT now();
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ResearchedBrandAliases" DROP COLUMN IF EXISTS "CreatedAt";
ALTER TABLE "ResearchedBrandAliases" DROP COLUMN IF EXISTS "Status";
ALTER TABLE "ResearchedBrandAliases" DROP COLUMN IF EXISTS "AliasKind";
ALTER TABLE "ResearchedBrandAliases" DROP COLUMN IF EXISTS "Confidence";
ALTER TABLE "ResearchedBrandAliases" DROP COLUMN IF EXISTS "Source";
""");
    }
}
