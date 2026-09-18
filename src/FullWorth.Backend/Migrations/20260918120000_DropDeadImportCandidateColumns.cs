using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Entfernt zwei tote Spalten aus "ImportCandidates" (#142): "RawSourceEncrypted" und "ValueDate".
///
/// Beide kamen mit 20260830150000_FullFeatureParity und wurden seither von keinem INSERT befuellt und
/// von keinem SELECT gelesen - ImportJobStore.cs und ImportMappingStore.cs, die einzigen zwei Stellen,
/// die in diese Tabelle schreiben, kennen beide Spalten nicht.
///
/// "ImportCandidates" ist keine EF-Entitaet (reiner RawSql-Zugriff aus Modules/Import, kein DbSet in
/// FullWorthDbContext), deshalb gibt es dafuer keine C#-Modelleigenschaft zu entfernen und keine
/// Aenderung am Model-Snapshot - "ModelSnapshotMatchesCurrentModel" bleibt davon unberuehrt.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260918120000_DropDeadImportCandidateColumns")]
public sealed class DropDeadImportCandidateColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "ImportCandidates" DROP COLUMN IF EXISTS "RawSourceEncrypted";
ALTER TABLE "ImportCandidates" DROP COLUMN IF EXISTS "ValueDate";
""");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE "ImportCandidates" ADD COLUMN IF NOT EXISTS "ValueDate" date NULL;
ALTER TABLE "ImportCandidates" ADD COLUMN IF NOT EXISTS "RawSourceEncrypted" text NULL;
""");
}
