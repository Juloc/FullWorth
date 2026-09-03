using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260902065000_CloudTransportFoundation")]
public sealed class CloudTransportFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "CloudConnectionStates" ADD COLUMN IF NOT EXISTS "ScopeKey" character varying(32) NULL;
ALTER TABLE "CloudConnectionStates" ADD COLUMN IF NOT EXISTS "LastRegistrationAt" timestamp with time zone NULL;
ALTER TABLE "CloudConnectionStates" ADD COLUMN IF NOT EXISTS "EntitlementStatus" character varying(80) NULL;

-- Keep the oldest state/InstanceId as the stable local instance identity if older builds ever created
-- multiple rows concurrently before ScopeKey existed.
WITH keeper AS (
    SELECT "Id" FROM "CloudConnectionStates" ORDER BY "CreatedAt", "Id" LIMIT 1
)
DELETE FROM "CloudConnectionStates"
WHERE "Id" NOT IN (SELECT "Id" FROM keeper);

UPDATE "CloudConnectionStates" SET "ScopeKey" = 'instance' WHERE "ScopeKey" IS NULL;
ALTER TABLE "CloudConnectionStates" ALTER COLUMN "ScopeKey" SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CloudConnectionStates_ScopeKey" ON "CloudConnectionStates" ("ScopeKey");

CREATE TABLE IF NOT EXISTS "CloudInstanceCredentials" (
    "Id" uuid NOT NULL,
    "InstanceId" uuid NOT NULL,
    "ProtectedSecret" text NOT NULL,
    "SecretFingerprint" character varying(80) NOT NULL,
    "IssuedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NULL,
    "LastUsedAt" timestamp with time zone NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_CloudInstanceCredentials" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_CloudInstanceCredentials_InstanceId"
    ON "CloudInstanceCredentials" ("InstanceId");

ALTER TABLE "CloudSubmissionOutbox" ADD COLUMN IF NOT EXISTS "LeaseOwner" character varying(160) NULL;
ALTER TABLE "CloudSubmissionOutbox" ADD COLUMN IF NOT EXISTS "LeaseExpiresAt" timestamp with time zone NULL;
CREATE INDEX IF NOT EXISTS "IX_CloudSubmissionOutbox_LeaseOwner_LeaseExpiresAt"
    ON "CloudSubmissionOutbox" ("LeaseOwner", "LeaseExpiresAt");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP INDEX IF EXISTS "IX_CloudSubmissionOutbox_LeaseOwner_LeaseExpiresAt";
ALTER TABLE "CloudSubmissionOutbox" DROP COLUMN IF EXISTS "LeaseExpiresAt";
ALTER TABLE "CloudSubmissionOutbox" DROP COLUMN IF EXISTS "LeaseOwner";
DROP TABLE IF EXISTS "CloudInstanceCredentials";
DROP INDEX IF EXISTS "IX_CloudConnectionStates_ScopeKey";
ALTER TABLE "CloudConnectionStates" DROP COLUMN IF EXISTS "EntitlementStatus";
ALTER TABLE "CloudConnectionStates" DROP COLUMN IF EXISTS "LastRegistrationAt";
ALTER TABLE "CloudConnectionStates" DROP COLUMN IF EXISTS "ScopeKey";
""");
    }
}
