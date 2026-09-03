using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Web.Migrations;

public partial class SessionsAndRecoveryCodes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsDisabled",
            schema: "auth",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "RecoveryCodes",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AuthUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CodeHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RecoveryCodes", x => x.Id);
                table.CheckConstraint("CK_RecoveryCodes_CodeHash_Length", "octet_length(\"CodeHash\") = 32");
                table.ForeignKey(
                    name: "FK_RecoveryCodes_AspNetUsers_AuthUserId",
                    column: x => x.AuthUserId,
                    principalSchema: "auth",
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserSessions",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AuthUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DeviceName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                SecurityStampAtIssue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserSessions_AspNetUsers_AuthUserId",
                    column: x => x.AuthUserId,
                    principalSchema: "auth",
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RecoveryCodes_AuthUserId_CodeHash",
            schema: "auth",
            table: "RecoveryCodes",
            columns: new[] { "AuthUserId", "CodeHash" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserSessions_AbsoluteExpiresAt",
            schema: "auth",
            table: "UserSessions",
            column: "AbsoluteExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_UserSessions_AuthUserId",
            schema: "auth",
            table: "UserSessions",
            column: "AuthUserId");

        migrationBuilder.CreateIndex(
            name: "IX_UserSessions_RevokedAt",
            schema: "auth",
            table: "UserSessions",
            column: "RevokedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RecoveryCodes", schema: "auth");
        migrationBuilder.DropTable(name: "UserSessions", schema: "auth");
        migrationBuilder.DropColumn(name: "IsDisabled", schema: "auth", table: "AspNetUsers");
    }
}
