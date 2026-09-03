using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDedups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationDedups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FinanceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDedups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDedups_Users_FinanceUserId",
                        column: x => x.FinanceUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDedups_FullWorthSpaceId",
                table: "NotificationDedups",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDedups_FinanceUserId_Type_DedupKey",
                table: "NotificationDedups",
                columns: new[] { "FinanceUserId", "Type", "DedupKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDedups");
        }
    }
}
