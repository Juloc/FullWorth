using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260901193000_IntelligenceFoundation")]
public sealed class IntelligenceFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE "AiCredentials" (
    "Id" uuid NOT NULL,
    "OwnerUserId" uuid NULL,
    "Provider" character varying(40) NOT NULL,
    "Name" character varying(120) NOT NULL,
    "ProtectedSecret" text NOT NULL,
    "SecretFingerprint" character varying(80) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "LastTestedAt" timestamp with time zone NULL,
    "LastTestSucceeded" boolean NULL,
    CONSTRAINT "PK_AiCredentials" PRIMARY KEY ("Id")
);
CREATE INDEX "IX_AiCredentials_OwnerUserId_Provider_Name" ON "AiCredentials" ("OwnerUserId", "Provider", "Name");

CREATE TABLE "AiInstanceSettings" (
    "Id" uuid NOT NULL,
    "Enabled" boolean NOT NULL,
    "Provider" character varying(40) NOT NULL,
    "CredentialId" uuid NULL,
    "AllowUserCredentials" boolean NOT NULL,
    "DefaultTextModel" character varying(120) NOT NULL,
    "DefaultVisionModel" character varying(120) NOT NULL,
    "DailyBudgetEur" numeric(18,4) NULL,
    "MonthlyBudgetEur" numeric(18,4) NULL,
    "DailyScanEnabled" boolean NOT NULL,
    "WeeklyDeepScanEnabled" boolean NOT NULL,
    "MonthlyReviewEnabled" boolean NOT NULL,
    "ReceiptAiEnabled" boolean NOT NULL,
    "MerchantAiEnabled" boolean NOT NULL,
    "CategoryAiEnabled" boolean NOT NULL,
    "ContractAiEnabled" boolean NOT NULL,
    "ProductAiEnabled" boolean NOT NULL,
    "LogoResearchEnabled" boolean NOT NULL,
    "InternetResearchEnabled" boolean NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AiInstanceSettings" PRIMARY KEY ("Id")
);

CREATE TABLE "AiUserSettings" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Enabled" boolean NOT NULL,
    "CredentialId" uuid NULL,
    "TextModel" character varying(120) NULL,
    "VisionModel" character varying(120) NULL,
    "ReceiptAiEnabled" boolean NULL,
    "MerchantAiEnabled" boolean NULL,
    "CategoryAiEnabled" boolean NULL,
    "ContractAiEnabled" boolean NULL,
    "ProductAiEnabled" boolean NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AiUserSettings" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX "IX_AiUserSettings_UserId" ON "AiUserSettings" ("UserId");

CREATE TABLE "AiRuns" (
    "Id" uuid NOT NULL,
    "UserId" uuid NULL,
    "FullWorthSpaceId" uuid NULL,
    "Provider" character varying(40) NOT NULL,
    "Model" character varying(120) NOT NULL,
    "Capability" character varying(80) NOT NULL,
    "JobType" character varying(80) NOT NULL,
    "Status" character varying(32) NOT NULL,
    "StartedAt" timestamp with time zone NOT NULL,
    "CompletedAt" timestamp with time zone NULL,
    "InputItemCount" integer NOT NULL,
    "OutputItemCount" integer NOT NULL,
    "InputTokens" bigint NULL,
    "OutputTokens" bigint NULL,
    "EstimatedCostEur" numeric(18,6) NULL,
    "ActualCostEur" numeric(18,6) NULL,
    "CorrelationId" character varying(80) NOT NULL,
    "ErrorSummary" character varying(2000) NULL,
    CONSTRAINT "PK_AiRuns" PRIMARY KEY ("Id")
);
CREATE INDEX "IX_AiRuns_StartedAt" ON "AiRuns" ("StartedAt");
CREATE INDEX "IX_AiRuns_UserId_StartedAt" ON "AiRuns" ("UserId", "StartedAt");

CREATE TABLE "AiRunItems" (
    "Id" uuid NOT NULL,
    "RunId" uuid NOT NULL,
    "SubjectType" character varying(80) NOT NULL,
    "SubjectId" character varying(160) NOT NULL,
    "InputSummaryJson" jsonb NOT NULL,
    "OutputSummaryJson" jsonb NOT NULL,
    "Status" character varying(32) NOT NULL,
    "ErrorCode" character varying(120) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AiRunItems" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AiRunItems_AiRuns_RunId" FOREIGN KEY ("RunId") REFERENCES "AiRuns" ("Id") ON DELETE CASCADE
);
CREATE INDEX "IX_AiRunItems_RunId" ON "AiRunItems" ("RunId");

CREATE TABLE "IntelligenceSuggestions" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NULL,
    "UserId" uuid NULL,
    "Type" character varying(80) NOT NULL,
    "SubjectType" character varying(80) NOT NULL,
    "SubjectId" character varying(160) NOT NULL,
    "SemanticKey" character varying(240) NOT NULL,
    "ProposedPayloadJson" jsonb NOT NULL,
    "EvidenceJson" jsonb NOT NULL,
    "Provider" character varying(40) NOT NULL,
    "Model" character varying(120) NOT NULL,
    "Confidence" numeric(6,5) NOT NULL,
    "Status" character varying(32) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ReviewedAt" timestamp with time zone NULL,
    "ReviewedByUserId" uuid NULL,
    "RunId" uuid NULL,
    CONSTRAINT "PK_IntelligenceSuggestions" PRIMARY KEY ("Id")
);
CREATE INDEX "IX_IntelligenceSuggestions_Status_CreatedAt" ON "IntelligenceSuggestions" ("Status", "CreatedAt");
CREATE INDEX "IX_IntelligenceSuggestions_FullWorthSpaceId_Status" ON "IntelligenceSuggestions" ("FullWorthSpaceId", "Status");
CREATE INDEX "IX_IntelligenceSuggestions_SubjectType_SubjectId_SemanticKey_Status" ON "IntelligenceSuggestions" ("SubjectType", "SubjectId", "SemanticKey", "Status");

CREATE TABLE "IntelligenceFeedbackEvents" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "EventType" character varying(80) NOT NULL,
    "SubjectType" character varying(80) NOT NULL,
    "SubjectId" character varying(160) NOT NULL,
    "SubjectFingerprint" character varying(160) NOT NULL,
    "OldValueJson" jsonb NOT NULL,
    "NewValueJson" jsonb NOT NULL,
    "Source" character varying(40) NOT NULL,
    "CloudEligible" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_IntelligenceFeedbackEvents" PRIMARY KEY ("Id")
);
CREATE INDEX "IX_IntelligenceFeedbackEvents_FullWorthSpaceId_CreatedAt" ON "IntelligenceFeedbackEvents" ("FullWorthSpaceId", "CreatedAt");
CREATE INDEX "IX_IntelligenceFeedbackEvents_CloudEligible_CreatedAt" ON "IntelligenceFeedbackEvents" ("CloudEligible", "CreatedAt");

CREATE TABLE "IntelligenceJobs" (
    "Id" uuid NOT NULL,
    "Type" character varying(80) NOT NULL,
    "ScopeKey" character varying(160) NOT NULL,
    "ScheduledFor" timestamp with time zone NOT NULL,
    "IdempotencyKey" character varying(240) NOT NULL,
    "StartedAt" timestamp with time zone NULL,
    "CompletedAt" timestamp with time zone NULL,
    "Status" character varying(32) NOT NULL,
    "RetryCount" integer NOT NULL,
    "NextRetryAt" timestamp with time zone NULL,
    "ErrorCode" character varying(120) NULL,
    "PayloadJson" jsonb NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_IntelligenceJobs" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX "IX_IntelligenceJobs_IdempotencyKey" ON "IntelligenceJobs" ("IdempotencyKey");
CREATE INDEX "IX_IntelligenceJobs_Status_ScheduledFor" ON "IntelligenceJobs" ("Status", "ScheduledFor");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "IntelligenceJobs";
DROP TABLE IF EXISTS "IntelligenceFeedbackEvents";
DROP TABLE IF EXISTS "IntelligenceSuggestions";
DROP TABLE IF EXISTS "AiRunItems";
DROP TABLE IF EXISTS "AiRuns";
DROP TABLE IF EXISTS "AiUserSettings";
DROP TABLE IF EXISTS "AiInstanceSettings";
DROP TABLE IF EXISTS "AiCredentials";
""");
    }
}
