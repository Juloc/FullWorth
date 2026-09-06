using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260906090000_CloudLearningOutbox")]
public sealed class CloudLearningOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
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
    CONSTRAINT "PK_CloudSubmissionOutbox" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_IdempotencyKey"
    ON "CloudSubmissionOutbox" ("IdempotencyKey");
CREATE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_FeedbackEventId"
    ON "CloudSubmissionOutbox" ("FeedbackEventId");
CREATE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_Status_NextAttemptAt_CreatedAt"
    ON "CloudSubmissionOutbox" ("Status", "NextAttemptAt", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_LeaseOwner_LeaseExpiresAt"
    ON "CloudSubmissionOutbox" ("LeaseOwner", "LeaseExpiresAt");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "CloudSubmissionOutbox";""");
    }
}
