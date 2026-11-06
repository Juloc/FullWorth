using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Die Merkliste fuer Vormerkungen, die durch ihre Buchung ersetzt wurden (siehe
/// <c>SupersededPendingTransaction</c>). Schluessel ist (Konto, ExternalKey): derselbe Weg, auf dem der
/// Abruf eine Zeile wiederfindet.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20261106110000_SupersededPendingTransactions")]
public sealed class SupersededPendingTransactions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SupersededPendingTransactions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SupersededPendingTransactions", x => x.Id);
                table.ForeignKey(
                    name: "FK_SupersededPendingTransactions_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_SupersededPendingTransactions_FullWorthSpaces_FullWorthSpaceId",
                    column: x => x.FullWorthSpaceId,
                    principalTable: "FullWorthSpaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SupersededPendingTransactions_AccountId_ExternalKey",
            table: "SupersededPendingTransactions",
            columns: ["AccountId", "ExternalKey"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SupersededPendingTransactions_FullWorthSpaceId",
            table: "SupersededPendingTransactions",
            column: "FullWorthSpaceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "SupersededPendingTransactions");
}
