using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260905114500_EnableBankingProfiles")]
public sealed class EnableBankingProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "EnableBankingProfiles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ApplicationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PrivateKeyPem = table.Column<string>(type: "text", nullable: false),
                KeyFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Environment = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ApplicationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                ServicesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                RedirectUrlsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EnableBankingProfiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_EnableBankingProfiles_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_EnableBankingProfiles_UserId",
            table: "EnableBankingProfiles",
            column: "UserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_EnableBankingProfiles_ApplicationId",
            table: "EnableBankingProfiles",
            column: "ApplicationId");

        migrationBuilder.AddColumn<Guid>(
            name: "EnableBankingProfileId",
            table: "BankConnections",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PsuType",
            table: "BankConnections",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "personal");

        migrationBuilder.AddColumn<string>(
            name: "AuthMethod",
            table: "BankConnections",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RequiredPsuHeadersJson",
            table: "BankConnections",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        migrationBuilder.CreateIndex(
            name: "IX_BankConnections_EnableBankingProfileId",
            table: "BankConnections",
            column: "EnableBankingProfileId");

        migrationBuilder.AddForeignKey(
            name: "FK_BankConnections_EnableBankingProfiles_EnableBankingProfileId",
            table: "BankConnections",
            column: "EnableBankingProfileId",
            principalTable: "EnableBankingProfiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_BankConnections_EnableBankingProfiles_EnableBankingProfileId",
            table: "BankConnections");

        migrationBuilder.DropIndex(
            name: "IX_BankConnections_EnableBankingProfileId",
            table: "BankConnections");

        migrationBuilder.DropColumn(name: "EnableBankingProfileId", table: "BankConnections");
        migrationBuilder.DropColumn(name: "PsuType", table: "BankConnections");
        migrationBuilder.DropColumn(name: "AuthMethod", table: "BankConnections");
        migrationBuilder.DropColumn(name: "RequiredPsuHeadersJson", table: "BankConnections");

        migrationBuilder.DropTable(name: "EnableBankingProfiles");
    }
}
