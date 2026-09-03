using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FullWorth.Backend.Migrations;

public partial class InitialFinanceSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

        migrationBuilder.CreateTable(
            name: "Accounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BankConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "text", nullable: false),
                IdentificationHash = table.Column<string>(type: "text", nullable: false),
                ProviderAccountId = table.Column<string>(type: "text", nullable: false),
                InstitutionName = table.Column<string>(type: "text", nullable: false),
                DisplayName = table.Column<string>(type: "text", nullable: false),
                Product = table.Column<string>(type: "text", nullable: true),
                AccountType = table.Column<string>(type: "text", nullable: true),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                IbanLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                IncludeInNetWorth = table.Column<bool>(type: "boolean", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Accounts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Assets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Kind = table.Column<string>(type: "text", nullable: false),
                CurrentValue = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                ValuedAt = table.Column<DateOnly>(type: "date", nullable: true),
                AnnualGrowthRate = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: true),
                IncludeInNetWorth = table.Column<bool>(type: "boolean", nullable: false),
                Notes = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Assets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BalanceSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                BalanceType = table.Column<string>(type: "text", nullable: false),
                ReferenceDate = table.Column<DateOnly>(type: "date", nullable: true),
                CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BalanceSnapshots", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BankConnections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                InstitutionName = table.Column<string>(type: "text", nullable: false),
                Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                AuthorizationState = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                AuthorizationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ProviderSessionId = table.Column<string>(type: "text", nullable: true),
                Status = table.Column<string>(type: "text", nullable: false),
                ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                NextSyncAllowedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BankConnections", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Budgets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                Amount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Period = table.Column<string>(type: "text", nullable: false),
                CarryOver = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Budgets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Categories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                Icon = table.Column<string>(type: "text", nullable: true),
                IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Categories", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CategorizationRules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                Target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                MatchField = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                MatchMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Pattern = table.Column<string>(type: "text", nullable: false),
                Direction = table.Column<string>(type: "text", nullable: false),
                MinAmount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: true),
                MaxAmount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: true),
                MerchantCategoryCode = table.Column<string>(type: "text", nullable: true),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                MarkAsTransfer = table.Column<bool>(type: "boolean", nullable: false),
                StopProcessing = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CategorizationRules", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Contracts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                ProviderName = table.Column<string>(type: "text", nullable: true),
                Kind = table.Column<string>(type: "text", nullable: false),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                Amount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                BillingCycle = table.Column<string>(type: "text", nullable: false),
                Interval = table.Column<int>(type: "integer", nullable: false),
                StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                NextDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                AutoDetected = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                Notes = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Contracts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Liabilities",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Kind = table.Column<string>(type: "text", nullable: false),
                CurrentBalance = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                InterestRate = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: true),
                RegularPayment = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: true),
                PaymentCycle = table.Column<string>(type: "text", nullable: false),
                NextDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                IncludeInNetWorth = table.Column<bool>(type: "boolean", nullable: false),
                Notes = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Liabilities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "NetWorthSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Date = table.Column<DateOnly>(type: "date", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                Accounts = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Assets = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Liabilities = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                NetWorth = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NetWorthSnapshots", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Purchases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Merchant = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                ExternalOrderId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                PurchaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                TotalAmount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                MatchConfidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                ReceiptImagePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                SourceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Purchases", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Transactions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                ExternalKey = table.Column<string>(type: "text", nullable: false),
                ProviderTransactionId = table.Column<string>(type: "text", nullable: true),
                Status = table.Column<string>(type: "text", nullable: false),
                BookingDate = table.Column<DateOnly>(type: "date", nullable: true),
                ValueDate = table.Column<DateOnly>(type: "date", nullable: true),
                Amount = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Counterparty = table.Column<string>(type: "text", nullable: true),
                NormalizedCounterparty = table.Column<string>(type: "text", nullable: true),
                Description = table.Column<string>(type: "text", nullable: true),
                MerchantCategoryCode = table.Column<string>(type: "text", nullable: true),
                EntryReference = table.Column<string>(type: "text", nullable: true),
                UserNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                IsIgnored = table.Column<bool>(type: "boolean", nullable: false),
                IsTransfer = table.Column<bool>(type: "boolean", nullable: false),
                CategorizationSource = table.Column<string>(type: "text", nullable: false),
                RawJson = table.Column<string>(type: "jsonb", nullable: false),
                FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Transactions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Brand = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                Sku = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Asin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                Quantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                UnitPrice = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: true),
                TotalPrice = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                CategorizationSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_PurchaseItems_Purchases_PurchaseId",
                    column: x => x.PurchaseId,
                    principalTable: "Purchases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_Accounts_BankConnectionId", table: "Accounts", column: "BankConnectionId");
        migrationBuilder.CreateIndex(name: "IX_Accounts_Provider_IdentificationHash", table: "Accounts", columns: new[] { "Provider", "IdentificationHash" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_BalanceSnapshots_AccountId_CapturedAt", table: "BalanceSnapshots", columns: new[] { "AccountId", "CapturedAt" });
        migrationBuilder.CreateIndex(name: "IX_BankConnections_AuthorizationState", table: "BankConnections", column: "AuthorizationState", unique: true);
        migrationBuilder.CreateIndex(name: "IX_BankConnections_Provider_ProviderSessionId", table: "BankConnections", columns: new[] { "Provider", "ProviderSessionId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_Budgets_IsActive_CategoryId", table: "Budgets", columns: new[] { "IsActive", "CategoryId" });
        migrationBuilder.CreateIndex(name: "IX_Categories_Key", table: "Categories", column: "Key", unique: true);
        migrationBuilder.CreateIndex(name: "IX_CategorizationRules_Target_IsEnabled_Priority", table: "CategorizationRules", columns: new[] { "Target", "IsEnabled", "Priority" });
        migrationBuilder.CreateIndex(name: "IX_Contracts_IsActive_NextDueDate", table: "Contracts", columns: new[] { "IsActive", "NextDueDate" });
        migrationBuilder.CreateIndex(name: "IX_NetWorthSnapshots_Date_Currency", table: "NetWorthSnapshots", columns: new[] { "Date", "Currency" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_PurchaseItems_Asin", table: "PurchaseItems", column: "Asin");
        migrationBuilder.CreateIndex(name: "IX_PurchaseItems_CategoryId", table: "PurchaseItems", column: "CategoryId");
        migrationBuilder.CreateIndex(name: "IX_PurchaseItems_PurchaseId", table: "PurchaseItems", column: "PurchaseId");
        migrationBuilder.CreateIndex(name: "IX_PurchaseItems_Sku", table: "PurchaseItems", column: "Sku");
        migrationBuilder.CreateIndex(name: "IX_Purchases_PurchaseDate", table: "Purchases", column: "PurchaseDate");
        migrationBuilder.CreateIndex(name: "IX_Purchases_Source_ExternalOrderId", table: "Purchases", columns: new[] { "Source", "ExternalOrderId" });
        migrationBuilder.CreateIndex(name: "IX_Purchases_TransactionId", table: "Purchases", column: "TransactionId");
        migrationBuilder.CreateIndex(name: "IX_Transactions_AccountId_ExternalKey", table: "Transactions", columns: new[] { "AccountId", "ExternalKey" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_Transactions_BookingDate", table: "Transactions", column: "BookingDate");
        migrationBuilder.CreateIndex(name: "IX_Transactions_CategoryId", table: "Transactions", column: "CategoryId");
        migrationBuilder.CreateIndex(name: "IX_Transactions_NormalizedCounterparty", table: "Transactions", column: "NormalizedCounterparty");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Accounts");
        migrationBuilder.DropTable(name: "Assets");
        migrationBuilder.DropTable(name: "BalanceSnapshots");
        migrationBuilder.DropTable(name: "BankConnections");
        migrationBuilder.DropTable(name: "Budgets");
        migrationBuilder.DropTable(name: "Categories");
        migrationBuilder.DropTable(name: "CategorizationRules");
        migrationBuilder.DropTable(name: "Contracts");
        migrationBuilder.DropTable(name: "Liabilities");
        migrationBuilder.DropTable(name: "NetWorthSnapshots");
        migrationBuilder.DropTable(name: "PurchaseItems");
        migrationBuilder.DropTable(name: "Transactions");
        migrationBuilder.DropTable(name: "Purchases");
    }
}
