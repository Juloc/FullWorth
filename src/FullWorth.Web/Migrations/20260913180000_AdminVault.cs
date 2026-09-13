using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Web.Migrations;

/// <summary>
/// The step-up that stands between an administrator's session and the secrets this installation holds.
///
/// Two tables and no column on <c>UserSessions</c>, deliberately: that row is written on every single
/// request, and an elevation must not add work there. The binding to a session is a foreign key in
/// spirit rather than in schema — the check is a join, so everything that already ends a session ends
/// the elevation with it.
///
/// <c>AdminElevationLockouts</c> is separate from the Identity lockout on purpose. Counting vault
/// failures into the sign-in lockout would let somebody who already holds a session lock the real
/// administrator out of signing in while their own stolen session keeps working.
/// </summary>
public partial class AdminVault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AdminElevations",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AuthUserId = table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevealsUsed = table.Column<int>(type: "integer", nullable: false),
                Factor = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AdminElevations", x => x.Id);
                table.ForeignKey(
                    name: "FK_AdminElevations_AspNetUsers_AuthUserId",
                    column: x => x.AuthUserId,
                    principalSchema: "auth",
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AdminElevations_AuthUserId_SessionId",
            schema: "auth",
            table: "AdminElevations",
            columns: ["AuthUserId", "SessionId"]);

        migrationBuilder.CreateTable(
            name: "AdminElevationLockouts",
            schema: "auth",
            columns: table => new
            {
                AuthUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Failures = table.Column<int>(type: "integer", nullable: false),
                LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AdminElevationLockouts", x => x.AuthUserId);
                table.ForeignKey(
                    name: "FK_AdminElevationLockouts_AspNetUsers_AuthUserId",
                    column: x => x.AuthUserId,
                    principalSchema: "auth",
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AdminElevations", schema: "auth");
        migrationBuilder.DropTable(name: "AdminElevationLockouts", schema: "auth");
    }
}
