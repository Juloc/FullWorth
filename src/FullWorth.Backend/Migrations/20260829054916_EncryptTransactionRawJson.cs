using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class EncryptTransactionRawJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // jsonb -> text needs an explicit USING cast on PostgreSQL; the column now stores encrypted
            // ciphertext (P0.4), not queryable JSON.
            migrationBuilder.Sql(@"ALTER TABLE ""Transactions"" ALTER COLUMN ""RawJson"" TYPE text USING ""RawJson""::text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only reversible while values are still valid JSON (pre-encryption rows).
            migrationBuilder.Sql(@"ALTER TABLE ""Transactions"" ALTER COLUMN ""RawJson"" TYPE jsonb USING ""RawJson""::jsonb;");
        }
    }
}
