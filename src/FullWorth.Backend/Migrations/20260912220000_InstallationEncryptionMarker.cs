using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Records which data encryption key this installation's rows were written with.
///
/// A derived fingerprint, never the key — so it can sit in the same database as the ciphertext without
/// weakening it, and can be printed in a startup error an operator has to act on.
///
/// The failure it exists for is silent and irreversible: <c>data_encryption_key</c> lives in a Docker
/// volume and the app creates it when missing, so a container started against an existing database
/// with the wrong or an empty secrets volume generates a new key, starts, reports itself healthy — and
/// every encrypted column is unreadable from then on. A Postgres backup does not help, because what
/// was lost never lived in Postgres.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260912220000_InstallationEncryptionMarker")]
public sealed class InstallationEncryptionMarkerTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "InstallationEncryptionMarkers" (
  "Id" uuid NOT NULL,
  "ScopeKey" character varying(40) NOT NULL,
  "KeyFingerprint" character varying(80) NOT NULL,
  "FirstSeenAt" timestamp with time zone NOT NULL,
  CONSTRAINT "PK_InstallationEncryptionMarkers" PRIMARY KEY ("Id")
);
""");
        migrationBuilder.Sql("""
CREATE UNIQUE INDEX IF NOT EXISTS "IX_InstallationEncryptionMarkers_ScopeKey"
  ON "InstallationEncryptionMarkers" ("ScopeKey");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "InstallationEncryptionMarkers";""");
}
