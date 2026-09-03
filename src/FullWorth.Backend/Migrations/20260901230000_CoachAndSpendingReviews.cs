using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260901230000_CoachAndSpendingReviews")]
public sealed class CoachAndSpendingReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "SpendingReviews" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "TransactionId" uuid NOT NULL,
    "PurchaseId" uuid NULL,
    "Sentiment" character varying(16) NOT NULL,
    "ReasonsJson" jsonb NOT NULL DEFAULT '[]'::jsonb,
    "Note" character varying(500) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_SpendingReviews" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_SpendingReviews_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_SpendingReviews_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_SpendingReviews_Transactions_TransactionId" FOREIGN KEY ("TransactionId") REFERENCES "Transactions" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_SpendingReviews_Purchases_PurchaseId" FOREIGN KEY ("PurchaseId") REFERENCES "Purchases" ("Id") ON DELETE SET NULL,
    CONSTRAINT "CK_SpendingReviews_Sentiment" CHECK ("Sentiment" IN ('Negative','Neutral','Positive')),
    CONSTRAINT "CK_SpendingReviews_ReasonsJson" CHECK (jsonb_typeof("ReasonsJson") = 'array')
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SpendingReviews_FullWorthSpaceId_UserId_TransactionId"
    ON "SpendingReviews" ("FullWorthSpaceId", "UserId", "TransactionId");
CREATE INDEX IF NOT EXISTS "IX_SpendingReviews_FullWorthSpaceId_UserId_UpdatedAt"
    ON "SpendingReviews" ("FullWorthSpaceId", "UserId", "UpdatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_SpendingReviews_TransactionId"
    ON "SpendingReviews" ("TransactionId");
CREATE INDEX IF NOT EXISTS "IX_SpendingReviews_PurchaseId"
    ON "SpendingReviews" ("PurchaseId");

CREATE TABLE IF NOT EXISTS "CoachConversations" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Title" character varying(120) NOT NULL,
    "MascotId" character varying(50) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "ArchivedAt" timestamp with time zone NULL,
    CONSTRAINT "PK_CoachConversations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_CoachConversations_FullWorthSpaces_FullWorthSpaceId" FOREIGN KEY ("FullWorthSpaceId") REFERENCES "FullWorthSpaces" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_CoachConversations_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS "IX_CoachConversations_FullWorthSpaceId_UserId_UpdatedAt"
    ON "CoachConversations" ("FullWorthSpaceId", "UserId", "UpdatedAt" DESC);

CREATE TABLE IF NOT EXISTS "CoachMessages" (
    "Id" uuid NOT NULL,
    "ConversationId" uuid NOT NULL,
    "Role" character varying(16) NOT NULL,
    "Text" character varying(12000) NOT NULL,
    "Mode" character varying(20) NOT NULL,
    "FactsJson" jsonb NULL,
    "Provider" character varying(64) NULL,
    "Model" character varying(120) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_CoachMessages" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_CoachMessages_CoachConversations_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES "CoachConversations" ("Id") ON DELETE CASCADE,
    CONSTRAINT "CK_CoachMessages_Role" CHECK ("Role" IN ('User','Assistant')),
    CONSTRAINT "CK_CoachMessages_Mode" CHECK ("Mode" IN ('Deterministic','Ai')),
    CONSTRAINT "CK_CoachMessages_FactsJson" CHECK ("FactsJson" IS NULL OR jsonb_typeof("FactsJson") = 'object')
);

CREATE INDEX IF NOT EXISTS "IX_CoachMessages_ConversationId_CreatedAt"
    ON "CoachMessages" ("ConversationId", "CreatedAt");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "CoachMessages";
DROP TABLE IF EXISTS "CoachConversations";
DROP TABLE IF EXISTS "SpendingReviews";
""");
    }
}
