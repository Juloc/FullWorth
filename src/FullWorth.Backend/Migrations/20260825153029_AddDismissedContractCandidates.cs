using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddDismissedContractCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DismissedContractCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Counterparty = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DismissedContractCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DismissedContractCandidates_FullWorthSpaces_FullWorthSpaceId",
                        column: x => x.FullWorthSpaceId,
                        principalTable: "FullWorthSpaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DismissedContractCandidates_FullWorthSpaceId_Counterparty_Cur~",
                table: "DismissedContractCandidates",
                columns: new[] { "FullWorthSpaceId", "Counterparty", "Currency" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DismissedContractCandidates");
        }
    }
}
