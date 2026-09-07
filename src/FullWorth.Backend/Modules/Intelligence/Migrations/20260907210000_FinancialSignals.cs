using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260907210000_FinancialSignals")]
public sealed class FinancialSignals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "FinancialSignals" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Type" character varying(80) NOT NULL,
    "SubjectType" character varying(80) NOT NULL,
    "SubjectId" character varying(160) NOT NULL,
    "SemanticKey" character varying(300) NOT NULL,
    "Source" character varying(40) NOT NULL,
    "Severity" character varying(24) NOT NULL,
    "Confidence" numeric(6,5) NOT NULL,
    "ImpactAmount" numeric(18,4) NULL,
    "ImpactCurrency" character varying(8) NULL,
    "TitleKey" character varying(160) NOT NULL,
    "PayloadJson" jsonb NOT NULL,
    "EvidenceJson" jsonb NOT NULL,
    "RankScore" numeric(18,4) NOT NULL,
    "DetectedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "ValidUntil" timestamp with time zone NULL,
    "ResolvedAt" timestamp with time zone NULL,
    "Version" integer NOT NULL,
    CONSTRAINT "PK_FinancialSignals" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_FinancialSignals_UserId_FullWorthSpaceId_SemanticKey"
    ON "FinancialSignals" ("UserId", "FullWorthSpaceId", "SemanticKey");
CREATE INDEX IF NOT EXISTS "IX_FinancialSignals_UserId_FullWorthSpaceId_ResolvedAt"
    ON "FinancialSignals" ("UserId", "FullWorthSpaceId", "ResolvedAt");
CREATE INDEX IF NOT EXISTS "IX_FinancialSignals_UserId_FullWorthSpaceId_RankScore"
    ON "FinancialSignals" ("UserId", "FullWorthSpaceId", "RankScore");
CREATE INDEX IF NOT EXISTS "IX_FinancialSignals_ValidUntil"
    ON "FinancialSignals" ("ValidUntil");

CREATE TABLE IF NOT EXISTS "FinancialSignalStates" (
    "Id" uuid NOT NULL,
    "SignalId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "State" character varying(24) NOT NULL,
    "SnoozedUntil" timestamp with time zone NULL,
    "Feedback" character varying(32) NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_FinancialSignalStates" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_FinancialSignalStates_FinancialSignals_SignalId"
        FOREIGN KEY ("SignalId") REFERENCES "FinancialSignals" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_FinancialSignalStates_SignalId_UserId"
    ON "FinancialSignalStates" ("SignalId", "UserId");
CREATE INDEX IF NOT EXISTS "IX_FinancialSignalStates_UserId_State_SnoozedUntil"
    ON "FinancialSignalStates" ("UserId", "State", "SnoozedUntil");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "FinancialSignalStates";
DROP TABLE IF EXISTS "FinancialSignals";
""");
    }
}
