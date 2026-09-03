using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260902061500_IntelligenceDigests")]
public sealed class IntelligenceDigests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE "IntelligenceDigests" (
    "Id" uuid NOT NULL,
    "FullWorthSpaceId" uuid NOT NULL,
    "PeriodType" character varying(16) NOT NULL,
    "PeriodKey" character varying(32) NOT NULL,
    "PeriodStart" timestamp with time zone NOT NULL,
    "PeriodEnd" timestamp with time zone NOT NULL,
    "SummaryJson" jsonb NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_IntelligenceDigests" PRIMARY KEY ("Id")
);
CREATE UNIQUE INDEX "IX_IntelligenceDigests_FullWorthSpaceId_PeriodType_PeriodKey"
    ON "IntelligenceDigests" ("FullWorthSpaceId", "PeriodType", "PeriodKey");
CREATE INDEX "IX_IntelligenceDigests_FullWorthSpaceId_PeriodStart"
    ON "IntelligenceDigests" ("FullWorthSpaceId", "PeriodStart");
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"IntelligenceDigests\";");
    }
}
