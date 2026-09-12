using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Web.Migrations;

/// <summary>
/// Which external sign-in providers this installation offers gets a home in the database.
///
/// Google and Apple were six environment variables in the deploy stack's compose file, so turning a
/// login button on meant editing YAML and restarting the whole stack — and the compose file ended up
/// carrying credentials that say nothing about how the containers are wired.
///
/// Configuration still wins, so anyone who prefers environment variables keeps them untouched.
///
/// The two secrets are stored as data-protection ciphertext, the same way the Enable Banking private key
/// already is. <c>text</c>, not a bounded column: ciphertext length follows the key ring, not what was
/// typed, and an Apple .p8 key is a whole PEM block.
/// </summary>
public partial class ExternalAuthSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ExternalAuthSettings",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ScopeKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                GoogleClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                GoogleClientSecretProtected = table.Column<string>(type: "text", nullable: false),
                AppleServiceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                AppleTeamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ApplePrivateKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ApplePrivateKeyProtected = table.Column<string>(type: "text", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ExternalAuthSettings", x => x.Id));

        // Exactly one row: a second "instance" row would make which providers this installation offers
        // depend on insertion order.
        migrationBuilder.CreateIndex(
            name: "IX_ExternalAuthSettings_ScopeKey",
            schema: "auth",
            table: "ExternalAuthSettings",
            column: "ScopeKey",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ExternalAuthSettings", schema: "auth");
    }
}
