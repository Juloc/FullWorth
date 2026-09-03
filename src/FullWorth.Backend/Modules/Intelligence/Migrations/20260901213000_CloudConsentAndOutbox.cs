using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Modules.Intelligence.Migrations;

[DbContext(typeof(IntelligenceDbContext))]
[Migration("20260901213000_CloudConsentAndOutbox")]
public sealed class CloudConsentAndOutboxMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CloudConnectionStates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                EnabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSubmissionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastKnowledgePackCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastErrorCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CloudConnectionStates", x => x.Id));

        migrationBuilder.CreateTable(
            name: "CloudIntelligenceConsents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Locale = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ClientVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CloudIntelligenceConsents", x => x.Id));

        migrationBuilder.CreateTable(
            name: "CloudSubmissionOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                FeedbackEventId = table.Column<Guid>(type: "uuid", nullable: true),
                IdempotencyKey = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                SchemaVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CloudSubmissionOutbox", x => x.Id);
                table.ForeignKey(
                    name: "FK_CloudSubmissionOutbox_IntelligenceFeedbackEvents_FeedbackEventId",
                    column: x => x.FeedbackEventId,
                    principalTable: "IntelligenceFeedbackEvents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CloudConnectionStates_InstanceId",
            table: "CloudConnectionStates",
            column: "InstanceId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CloudIntelligenceConsents_InstanceId_AcceptedAt",
            table: "CloudIntelligenceConsents",
            columns: new[] { "InstanceId", "AcceptedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_CloudIntelligenceConsents_InstanceId_PolicyVersion_RevokedAt",
            table: "CloudIntelligenceConsents",
            columns: new[] { "InstanceId", "PolicyVersion", "RevokedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_CloudSubmissionOutbox_FeedbackEventId",
            table: "CloudSubmissionOutbox",
            column: "FeedbackEventId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CloudSubmissionOutbox_IdempotencyKey",
            table: "CloudSubmissionOutbox",
            column: "IdempotencyKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CloudSubmissionOutbox_Status_NextAttemptAt_CreatedAt",
            table: "CloudSubmissionOutbox",
            columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CloudSubmissionOutbox");
        migrationBuilder.DropTable(name: "CloudIntelligenceConsents");
        migrationBuilder.DropTable(name: "CloudConnectionStates");
    }
}
