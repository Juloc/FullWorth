using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddFullWorthSpaceInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FullWorthSpaceInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailNormalized = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    SpaceRole = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountGrantsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FullWorthSpaceInvites", x => x.Id);
                    table.CheckConstraint("CK_FullWorthSpaceInvites_Role", "\"SpaceRole\" IN ('owner', 'member')");
                    table.CheckConstraint("CK_FullWorthSpaceInvites_Status", "\"Status\" IN ('pending', 'claimed', 'revoked')");
                    table.ForeignKey(
                        name: "FK_FullWorthSpaceInvites_FullWorthSpaces_FullWorthSpaceId",
                        column: x => x.FullWorthSpaceId,
                        principalTable: "FullWorthSpaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FullWorthSpaceInvites_FullWorthSpaceId_Status",
                table: "FullWorthSpaceInvites",
                columns: new[] { "FullWorthSpaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FullWorthSpaceInvites_TokenHash",
                table: "FullWorthSpaceInvites",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FullWorthSpaceInvites");
        }
    }
}
