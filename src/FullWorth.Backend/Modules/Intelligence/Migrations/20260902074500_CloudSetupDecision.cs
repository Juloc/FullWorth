using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260902074500_CloudSetupDecision")]
public sealed class CloudSetupDecision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "CloudConnectionStates" ADD COLUMN IF NOT EXISTS "SetupDecisionAt" timestamp with time zone NULL;
ALTER TABLE "CloudConnectionStates" ADD COLUMN IF NOT EXISTS "SetupDecisionByUserId" uuid NULL;

-- Preserve decisions made by builds that predate the explicit setup-decision columns. EnabledAt or
-- DisabledAt proves that an administrator already acted; a newly-created default disabled row has
-- neither and therefore remains intentionally undecided.
UPDATE "CloudConnectionStates"
SET "SetupDecisionAt" = COALESCE("DisabledAt", "EnabledAt", "UpdatedAt")
WHERE "SetupDecisionAt" IS NULL
  AND ("EnabledAt" IS NOT NULL OR "DisabledAt" IS NOT NULL);

UPDATE "CloudConnectionStates" state
SET "SetupDecisionByUserId" = (
    SELECT c."AcceptedByUserId"
    FROM "CloudIntelligenceConsents" c
    WHERE c."InstanceId" = state."InstanceId"
    ORDER BY c."AcceptedAt" DESC
    LIMIT 1
)
WHERE state."SetupDecisionAt" IS NOT NULL
  AND state."SetupDecisionByUserId" IS NULL
  AND EXISTS (
      SELECT 1
      FROM "CloudIntelligenceConsents" c
      WHERE c."InstanceId" = state."InstanceId"
  );
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "CloudConnectionStates" DROP COLUMN IF EXISTS "SetupDecisionByUserId";
ALTER TABLE "CloudConnectionStates" DROP COLUMN IF EXISTS "SetupDecisionAt";
""");
    }
}
