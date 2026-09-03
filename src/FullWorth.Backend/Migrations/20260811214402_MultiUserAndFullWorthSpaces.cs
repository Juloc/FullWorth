using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations
{
    /// <inheritdoc />
    public partial class MultiUserAndFullWorthSpaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetWorthSnapshots_Date_Currency",
                table: "NetWorthSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Categories_Key",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_Provider_IdentificationHash",
                table: "Accounts");

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Purchases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "NetWorthSnapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "NetWorthSnapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Liabilities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Contracts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "CategorizationRules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Categories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Budgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "BankConnections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FullWorthSpaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaseCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FullWorthSpaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailNormalized = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountOwners",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnershipType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountOwners", x => new { x.AccountId, x.UserId });
                    table.CheckConstraint("CK_AccountOwners_OwnershipType", "\"OwnershipType\" IN ('owner', 'viewer')");
                    table.ForeignKey(
                        name: "FK_AccountOwners_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountOwners_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FullWorthSpaceMembers",
                columns: table => new
                {
                    FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FullWorthSpaceMembers", x => new { x.FullWorthSpaceId, x.UserId });
                    table.CheckConstraint("CK_FullWorthSpaceMembers_Role", "\"Role\" IN ('owner', 'member')");
                    table.ForeignKey(
                        name: "FK_FullWorthSpaceMembers_FullWorthSpaces_FullWorthSpaceId",
                        column: x => x.FullWorthSpaceId,
                        principalTable: "FullWorthSpaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FullWorthSpaceMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "FullWorthSpaces",
                columns: new[] { "Id", "BaseCurrency", "CreatedAt", "Name", "UpdatedAt" },
                values: new object[] { new Guid("7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1"), "EUR", new DateTimeOffset(new DateTime(2026, 8, 11, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Default", new DateTimeOffset(new DateTime(2026, 8, 11, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.Sql("UPDATE \"BankConnections\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Accounts\" a SET \"FullWorthSpaceId\" = b.\"FullWorthSpaceId\" FROM \"BankConnections\" b WHERE a.\"BankConnectionId\" = b.\"Id\" AND a.\"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Accounts\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Categories\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"CategorizationRules\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Contracts\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Budgets\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Assets\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Liabilities\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"NetWorthSnapshots\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");
            migrationBuilder.Sql("UPDATE \"Purchases\" SET \"FullWorthSpaceId\" = '7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1'::uuid WHERE \"FullWorthSpaceId\" IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Purchases",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "NetWorthSnapshots",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Liabilities",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Contracts",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "CategorizationRules",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Categories",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Budgets",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "BankConnections",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Assets",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FullWorthSpaceId",
                table: "Accounts",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_FullWorthSpaceId",
                table: "Purchases",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_FullWorthSpaceId_Source_ExternalOrderId",
                table: "Purchases",
                columns: new[] { "FullWorthSpaceId", "Source", "ExternalOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetWorthSnapshots_Date_Currency",
                table: "NetWorthSnapshots",
                columns: new[] { "Date", "Currency" });

            migrationBuilder.CreateIndex(
                name: "IX_NetWorthSnapshots_FullWorthSpaceId_UserId",
                table: "NetWorthSnapshots",
                columns: new[] { "FullWorthSpaceId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_NetWorthSnapshots_FullWorthSpaceId_UserId_Date_Currency",
                table: "NetWorthSnapshots",
                columns: new[] { "FullWorthSpaceId", "UserId", "Date", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetWorthSnapshots_UserId",
                table: "NetWorthSnapshots",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Liabilities_FullWorthSpaceId",
                table: "Liabilities",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_AccountId",
                table: "Contracts",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_CategoryId",
                table: "Contracts",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_FullWorthSpaceId",
                table: "Contracts",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_CategorizationRules_CategoryId",
                table: "CategorizationRules",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CategorizationRules_FullWorthSpaceId",
                table: "CategorizationRules",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_FullWorthSpaceId",
                table: "Categories",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_FullWorthSpaceId_Key",
                table: "Categories",
                columns: new[] { "FullWorthSpaceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Key",
                table: "Categories",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ParentId",
                table: "Categories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_CategoryId",
                table: "Budgets",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_FullWorthSpaceId",
                table: "Budgets",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_FullWorthSpaceId",
                table: "BankConnections",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_FullWorthSpaceId",
                table: "Assets",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_FullWorthSpaceId",
                table: "Accounts",
                column: "FullWorthSpaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_FullWorthSpaceId_Provider_IdentificationHash",
                table: "Accounts",
                columns: new[] { "FullWorthSpaceId", "Provider", "IdentificationHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Provider_IdentificationHash",
                table: "Accounts",
                columns: new[] { "Provider", "IdentificationHash" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountOwners_UserId_AccountId",
                table: "AccountOwners",
                columns: new[] { "UserId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_FullWorthSpaceMembers_FullWorthSpaceId_Role",
                table: "FullWorthSpaceMembers",
                columns: new[] { "FullWorthSpaceId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_FullWorthSpaceMembers_UserId_FullWorthSpaceId",
                table: "FullWorthSpaceMembers",
                columns: new[] { "UserId", "FullWorthSpaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_EmailNormalized",
                table: "Users",
                column: "EmailNormalized",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_BankConnections_BankConnectionId",
                table: "Accounts",
                column: "BankConnectionId",
                principalTable: "BankConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_FullWorthSpaces_FullWorthSpaceId",
                table: "Accounts",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_FullWorthSpaces_FullWorthSpaceId",
                table: "Assets",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BalanceSnapshots_Accounts_AccountId",
                table: "BalanceSnapshots",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BankConnections_FullWorthSpaces_FullWorthSpaceId",
                table: "BankConnections",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Budgets_Categories_CategoryId",
                table: "Budgets",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Budgets_FullWorthSpaces_FullWorthSpaceId",
                table: "Budgets",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Categories_Categories_ParentId",
                table: "Categories",
                column: "ParentId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Categories_FullWorthSpaces_FullWorthSpaceId",
                table: "Categories",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CategorizationRules_Categories_CategoryId",
                table: "CategorizationRules",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CategorizationRules_FullWorthSpaces_FullWorthSpaceId",
                table: "CategorizationRules",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_Accounts_AccountId",
                table: "Contracts",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_Categories_CategoryId",
                table: "Contracts",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_FullWorthSpaces_FullWorthSpaceId",
                table: "Contracts",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Liabilities_FullWorthSpaces_FullWorthSpaceId",
                table: "Liabilities",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_NetWorthSnapshots_FullWorthSpaces_FullWorthSpaceId",
                table: "NetWorthSnapshots",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_NetWorthSnapshots_Users_UserId",
                table: "NetWorthSnapshots",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseItems_Categories_CategoryId",
                table: "PurchaseItems",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Purchases_FullWorthSpaces_FullWorthSpaceId",
                table: "Purchases",
                column: "FullWorthSpaceId",
                principalTable: "FullWorthSpaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Purchases_Transactions_TransactionId",
                table: "Purchases",
                column: "TransactionId",
                principalTable: "Transactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Accounts_AccountId",
                table: "Transactions",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Categories_CategoryId",
                table: "Transactions",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_BankConnections_BankConnectionId",
                table: "Accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_FullWorthSpaces_FullWorthSpaceId",
                table: "Accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_Assets_FullWorthSpaces_FullWorthSpaceId",
                table: "Assets");

            migrationBuilder.DropForeignKey(
                name: "FK_BalanceSnapshots_Accounts_AccountId",
                table: "BalanceSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_BankConnections_FullWorthSpaces_FullWorthSpaceId",
                table: "BankConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_Budgets_Categories_CategoryId",
                table: "Budgets");

            migrationBuilder.DropForeignKey(
                name: "FK_Budgets_FullWorthSpaces_FullWorthSpaceId",
                table: "Budgets");

            migrationBuilder.DropForeignKey(
                name: "FK_Categories_Categories_ParentId",
                table: "Categories");

            migrationBuilder.DropForeignKey(
                name: "FK_Categories_FullWorthSpaces_FullWorthSpaceId",
                table: "Categories");

            migrationBuilder.DropForeignKey(
                name: "FK_CategorizationRules_Categories_CategoryId",
                table: "CategorizationRules");

            migrationBuilder.DropForeignKey(
                name: "FK_CategorizationRules_FullWorthSpaces_FullWorthSpaceId",
                table: "CategorizationRules");

            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_Accounts_AccountId",
                table: "Contracts");

            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_Categories_CategoryId",
                table: "Contracts");

            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_FullWorthSpaces_FullWorthSpaceId",
                table: "Contracts");

            migrationBuilder.DropForeignKey(
                name: "FK_Liabilities_FullWorthSpaces_FullWorthSpaceId",
                table: "Liabilities");

            migrationBuilder.DropForeignKey(
                name: "FK_NetWorthSnapshots_FullWorthSpaces_FullWorthSpaceId",
                table: "NetWorthSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_NetWorthSnapshots_Users_UserId",
                table: "NetWorthSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseItems_Categories_CategoryId",
                table: "PurchaseItems");

            migrationBuilder.DropForeignKey(
                name: "FK_Purchases_FullWorthSpaces_FullWorthSpaceId",
                table: "Purchases");

            migrationBuilder.DropForeignKey(
                name: "FK_Purchases_Transactions_TransactionId",
                table: "Purchases");

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Accounts_AccountId",
                table: "Transactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Categories_CategoryId",
                table: "Transactions");

            migrationBuilder.DropTable(
                name: "AccountOwners");

            migrationBuilder.DropTable(
                name: "FullWorthSpaceMembers");

            migrationBuilder.DropTable(
                name: "FullWorthSpaces");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Purchases_FullWorthSpaceId",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_Purchases_FullWorthSpaceId_Source_ExternalOrderId",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_NetWorthSnapshots_Date_Currency",
                table: "NetWorthSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_NetWorthSnapshots_FullWorthSpaceId_UserId",
                table: "NetWorthSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_NetWorthSnapshots_FullWorthSpaceId_UserId_Date_Currency",
                table: "NetWorthSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_NetWorthSnapshots_UserId",
                table: "NetWorthSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Liabilities_FullWorthSpaceId",
                table: "Liabilities");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_AccountId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_CategoryId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_FullWorthSpaceId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_CategorizationRules_CategoryId",
                table: "CategorizationRules");

            migrationBuilder.DropIndex(
                name: "IX_CategorizationRules_FullWorthSpaceId",
                table: "CategorizationRules");

            migrationBuilder.DropIndex(
                name: "IX_Categories_FullWorthSpaceId",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Categories_FullWorthSpaceId_Key",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Categories_Key",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Categories_ParentId",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Budgets_CategoryId",
                table: "Budgets");

            migrationBuilder.DropIndex(
                name: "IX_Budgets_FullWorthSpaceId",
                table: "Budgets");

            migrationBuilder.DropIndex(
                name: "IX_BankConnections_FullWorthSpaceId",
                table: "BankConnections");

            migrationBuilder.DropIndex(
                name: "IX_Assets_FullWorthSpaceId",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_FullWorthSpaceId",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_FullWorthSpaceId_Provider_IdentificationHash",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_Provider_IdentificationHash",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "NetWorthSnapshots");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "NetWorthSnapshots");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "Liabilities");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "CategorizationRules");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "Budgets");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "BankConnections");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "FullWorthSpaceId",
                table: "Accounts");

            migrationBuilder.CreateIndex(
                name: "IX_NetWorthSnapshots_Date_Currency",
                table: "NetWorthSnapshots",
                columns: new[] { "Date", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Key",
                table: "Categories",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Provider_IdentificationHash",
                table: "Accounts",
                columns: new[] { "Provider", "IdentificationHash" },
                unique: true);
        }
    }
}
