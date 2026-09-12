using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Where this installation records the pack verification key it trusts, per Cloud endpoint.
///
/// Until now that key could only arrive as a file an operator copied out of a Docker volume the Cloud
/// and the app stack shared. That is possible on one host and impossible everywhere else, which made
/// signed packs unverifiable for every self-hoster who is not also running the Cloud.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260912200000_KnowledgePackTrustedKeys")]
public sealed class KnowledgePackTrustedKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "KnowledgePackTrustedKeys" (
    "Id" uuid NOT NULL,
    "Endpoint" character varying(400) NOT NULL,
    "Algorithm" character varying(40) NOT NULL,
    "PublicKeyPem" text NOT NULL,
    "Fingerprint" character varying(120) NOT NULL,
    "PinnedAt" timestamp with time zone NOT NULL,
    "OfferedFingerprint" character varying(120) NULL,
    "OfferedAt" timestamp with time zone NULL,
    CONSTRAINT "PK_KnowledgePackTrustedKeys" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_KnowledgePackTrustedKeys_Endpoint"
    ON "KnowledgePackTrustedKeys" ("Endpoint");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "KnowledgePackTrustedKeys";""");
}
