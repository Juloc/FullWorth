using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Web.Migrations;

/// <summary>
/// Where the settings an administrator changes in the browser are kept.
///
/// One row per configuration key, and the key is the real one the app reads — not a translation of
/// it. That is what lets these values travel through the ordinary configuration chain instead of
/// needing a service per setting: the stored row is published into a source that sits between
/// appsettings.json and the environment variables, so appsettings loses and an explicit environment
/// variable still wins. One insert position, every key.
///
/// Value is text rather than a bounded column because a Secret-kind setting holds data-protection
/// ciphertext, whose length depends on the key ring rather than on what was typed.
/// </summary>
public partial class InstanceConfiguration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InstanceConfigurationValues",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Value = table.Column<string>(type: "text", nullable: false),
                IsProtected = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_InstanceConfigurationValues", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_InstanceConfigurationValues_Key",
            schema: "auth",
            table: "InstanceConfigurationValues",
            column: "Key",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "InstanceConfigurationValues", schema: "auth");
    }
}
