using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddManualAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "BankConnectionId",
                table: "Accounts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The scaffolded Down() would rewrite NULLs to Guid.Empty, which violates the FK to
            // BankConnections. Fail the rollback with an actionable message instead when manual
            // (connection-less) accounts exist.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Accounts" WHERE "BankConnectionId" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot revert AddManualAccounts: manual accounts exist (BankConnectionId IS NULL). Delete them first.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "BankConnectionId",
                table: "Accounts",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
