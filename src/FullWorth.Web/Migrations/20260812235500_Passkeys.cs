using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Web.Migrations;

public partial class Passkeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PasskeyChallenges",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AuthUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Type = table.Column<int>(type: "integer", nullable: false),
                OptionsJson = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PasskeyChallenges", x => x.Id);
                table.CheckConstraint("CK_PasskeyChallenges_Type", "\"Type\" IN (1, 2)");
                table.CheckConstraint("CK_PasskeyChallenges_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                table.CheckConstraint("CK_PasskeyChallenges_ConsumedAt", "\"ConsumedAt\" IS NULL OR \"ConsumedAt\" >= \"CreatedAt\"");
                table.ForeignKey(
                    name: "FK_PasskeyChallenges_AspNetUsers_AuthUserId",
                    column: x => x.AuthUserId,
                    principalSchema: "auth",
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PasskeyCredentials",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AuthUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CredentialId = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                PublicKey = table.Column<byte[]>(type: "bytea", maxLength: 4096, nullable: false),
                UserHandle = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                SignatureCounter = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Aaguid = table.Column<Guid>(type: "uuid", nullable: false),
                IsBackupEligible = table.Column<bool>(type: "boolean", nullable: false),
                IsBackedUp = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PasskeyCredentials", x => x.Id);
                table.ForeignKey(
                    name: "FK_PasskeyCredentials_AspNetUsers_AuthUserId",
                    column: x => x.AuthUserId,
                    principalSchema: "auth",
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_PasskeyChallenges_AuthUserId", schema: "auth", table: "PasskeyChallenges", column: "AuthUserId");
        migrationBuilder.CreateIndex(name: "IX_PasskeyChallenges_Type_ExpiresAt_ConsumedAt", schema: "auth", table: "PasskeyChallenges", columns: new[] { "Type", "ExpiresAt", "ConsumedAt" });
        migrationBuilder.CreateIndex(name: "IX_PasskeyCredentials_AuthUserId", schema: "auth", table: "PasskeyCredentials", column: "AuthUserId");
        migrationBuilder.CreateIndex(name: "IX_PasskeyCredentials_CredentialId", schema: "auth", table: "PasskeyCredentials", column: "CredentialId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PasskeyChallenges", schema: "auth");
        migrationBuilder.DropTable(name: "PasskeyCredentials", schema: "auth");
    }
}
