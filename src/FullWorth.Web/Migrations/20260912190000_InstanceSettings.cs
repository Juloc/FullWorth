using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Web.Migrations;

/// <summary>
/// The installation's public address gets a home in the database.
///
/// It was the last line a deployment had to edit by hand. It is not a credential and not an id — it is
/// "which address am I reached at", which the app learns the first time a human uses it: the first
/// registration happens on the real domain, through the real reverse proxy.
///
/// Create-only in the store, because the passkey relying party id is derived from it and changing a
/// relying party id makes every passkey already registered against it unusable.
/// </summary>
public partial class InstanceSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InstanceSettings",
            schema: "auth",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ScopeKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                PublicUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_InstanceSettings", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_InstanceSettings_ScopeKey",
            schema: "auth",
            table: "InstanceSettings",
            column: "ScopeKey",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "InstanceSettings", schema: "auth");
    }
}
