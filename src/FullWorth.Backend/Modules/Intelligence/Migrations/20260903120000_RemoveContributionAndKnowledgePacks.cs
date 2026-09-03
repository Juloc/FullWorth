using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

/// <summary>
/// Removes the cloud-contribution outbox and the knowledge-pack tables. Contribution and
/// knowledge-pack distribution now live outside this instance; only the minimal consent+connection
/// client remains. The state/consent/credential tables are intentionally left intact.
/// </summary>
[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260903120000_RemoveContributionAndKnowledgePacks")]
public sealed class RemoveContributionAndKnowledgePacks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "OfficialMerchantMappings";
DROP TABLE IF EXISTS "KnowledgePackArchives";
DROP TABLE IF EXISTS "KnowledgePackInstallations";
DROP TABLE IF EXISTS "CloudSubmissionOutbox";
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "CloudSubmissionOutbox" (
    "Id" uuid NOT NULL,
    "InstanceId" uuid NOT NULL,
    "FeedbackEventId" uuid NULL,
    "IdempotencyKey" character varying(240) NOT NULL,
    "SchemaVersion" character varying(40) NOT NULL,
    "EventType" character varying(80) NOT NULL,
    "PayloadJson" jsonb NOT NULL,
    "Status" character varying(32) NOT NULL,
    "AttemptCount" integer NOT NULL,
    "NextAttemptAt" timestamp with time zone NULL,
    "LastAttemptAt" timestamp with time zone NULL,
    "SentAt" timestamp with time zone NULL,
    "ErrorCode" character varying(120) NULL,
    "LeaseOwner" character varying(160) NULL,
    "LeaseExpiresAt" timestamp with time zone NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_CloudSubmissionOutbox" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_CloudSubmissionOutbox_IntelligenceFeedbackEvents_FeedbackEventId"
        FOREIGN KEY ("FeedbackEventId") REFERENCES "IntelligenceFeedbackEvents" ("Id") ON DELETE RESTRICT
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_FeedbackEventId"
    ON "CloudSubmissionOutbox" ("FeedbackEventId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_IdempotencyKey"
    ON "CloudSubmissionOutbox" ("IdempotencyKey");
CREATE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_Status_NextAttemptAt_CreatedAt"
    ON "CloudSubmissionOutbox" ("Status", "NextAttemptAt", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_LeaseOwner_LeaseExpiresAt"
    ON "CloudSubmissionOutbox" ("LeaseOwner", "LeaseExpiresAt");

CREATE TABLE IF NOT EXISTS "KnowledgePackInstallations" (
    "Id" uuid NOT NULL,
    "ScopeKey" character varying(32) NOT NULL,
    "PackId" character varying(120) NOT NULL,
    "Version" character varying(80) NOT NULL,
    "SchemaVersion" character varying(40) NOT NULL,
    "Region" character varying(32) NOT NULL,
    "ContentSha256" character varying(80) NOT NULL,
    "SignatureAlgorithm" character varying(40) NOT NULL,
    "MerchantMappingCount" integer NOT NULL,
    "InstalledAt" timestamp with time zone NOT NULL,
    "LastCheckedAt" timestamp with time zone NOT NULL,
    "LastErrorCode" character varying(120) NULL,
    CONSTRAINT "PK_KnowledgePackInstallations" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_KnowledgePackInstallations_ScopeKey"
    ON "KnowledgePackInstallations" ("ScopeKey");

CREATE TABLE IF NOT EXISTS "KnowledgePackArchives" (
    "Id" uuid NOT NULL,
    "PackId" character varying(120) NOT NULL,
    "Version" character varying(80) NOT NULL,
    "SchemaVersion" character varying(40) NOT NULL,
    "Region" character varying(32) NOT NULL,
    "ContentSha256" character varying(80) NOT NULL,
    "SignatureAlgorithm" character varying(40) NOT NULL,
    "SignatureBase64" text NOT NULL,
    "PayloadBase64" text NOT NULL,
    "VerifiedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_KnowledgePackArchives" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_KnowledgePackArchives_PackId_Version"
    ON "KnowledgePackArchives" ("PackId", "Version");

CREATE TABLE IF NOT EXISTS "OfficialMerchantMappings" (
    "Id" uuid NOT NULL,
    "PackId" character varying(120) NOT NULL,
    "PackVersion" character varying(80) NOT NULL,
    "AliasKey" character varying(300) NOT NULL,
    "Direction" character varying(16) NOT NULL,
    "CanonicalMerchantKey" character varying(180) NOT NULL,
    "CanonicalName" character varying(240) NOT NULL,
    "CategoryKey" character varying(180) NULL,
    "Country" character varying(8) NULL,
    "Confidence" numeric(6,5) NOT NULL,
    "Domain" character varying(255) NULL,
    "LogoKey" character varying(180) NULL,
    CONSTRAINT "PK_OfficialMerchantMappings" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfficialMerchantMappings_AliasKey_Direction_Country"
    ON "OfficialMerchantMappings" ("AliasKey", "Direction", "Country");
CREATE INDEX IF NOT EXISTS "IX_OfficialMerchantMappings_CanonicalMerchantKey"
    ON "OfficialMerchantMappings" ("CanonicalMerchantKey");
""");
    }
}
