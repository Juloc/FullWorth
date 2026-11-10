using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Das VAPID-Schluesselpaar fuer Web Push, von der Installation selbst erzeugt (siehe <c>PushKeyStore</c>).
/// Genau eine Zeile - der eindeutige Index auf <c>ScopeKey</c> erzwingt es.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20261110100000_PushInstanceKey")]
public sealed class PushInstanceKeyTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PushInstanceKeys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ScopeKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                PublicKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                PrivateKeyProtected = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_PushInstanceKeys", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_PushInstanceKeys_ScopeKey",
            table: "PushInstanceKeys",
            column: "ScopeKey",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "PushInstanceKeys");
}
