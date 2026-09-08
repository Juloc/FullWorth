using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260908062000_ActionProposals")]
public sealed class ActionProposals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS "ActionProposals" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Handler" character varying(80) NOT NULL,
    "State" character varying(24) NOT NULL,
    "PayloadJson" jsonb NOT NULL,
    "PreviewJson" jsonb NOT NULL,
    "PreviewToken" character varying(128) NOT NULL,
    "Source" character varying(40) NOT NULL,
    "SourceReference" character varying(200) NULL,
    "ResultJson" jsonb NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "ExecutedAt" timestamp with time zone NULL,
    "RejectedAt" timestamp with time zone NULL,
    "Version" integer NOT NULL,
    CONSTRAINT "PK_ActionProposals" PRIMARY KEY ("Id")
);

CREATE INDEX IF NOT EXISTS "IX_ActionProposals_UserId_FullWorthSpaceId_State_UpdatedAt"
    ON "ActionProposals" ("UserId", "FullWorthSpaceId", "State", "UpdatedAt");
CREATE INDEX IF NOT EXISTS "IX_ActionProposals_Handler_State"
    ON "ActionProposals" ("Handler", "State");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS "ActionProposals";
""");
    }
}
