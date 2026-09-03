using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class EncryptBankConnectionSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankConnections_Provider_ProviderSessionId",
                table: "BankConnections");

            migrationBuilder.AlterColumn<string>(
                name: "AuthorizationId",
                table: "BankConnections",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderSessionIdLookup",
                table: "BankConnections",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_Provider_ProviderSessionIdLookup",
                table: "BankConnections",
                columns: new[] { "Provider", "ProviderSessionIdLookup" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankConnections_Provider_ProviderSessionIdLookup",
                table: "BankConnections");

            migrationBuilder.DropColumn(
                name: "ProviderSessionIdLookup",
                table: "BankConnections");

            migrationBuilder.AlterColumn<string>(
                name: "AuthorizationId",
                table: "BankConnections",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_Provider_ProviderSessionId",
                table: "BankConnections",
                columns: new[] { "Provider", "ProviderSessionId" },
                unique: true);
        }
    }
}
