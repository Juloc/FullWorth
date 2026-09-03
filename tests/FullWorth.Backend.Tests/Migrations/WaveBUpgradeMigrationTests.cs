using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Coach;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FullWorth.Backend.Tests.Migrations;

public sealed class WaveBUpgradeMigrationTests
{
    private const string InitialMigration = "20260811104400_InitialFinanceSchema";

    [Fact]
    public async Task ExistingB0RowsUpgradeWithoutLossOrFakeIdentity()
    {
        var options = new DbContextOptionsBuilder<FullWorthDbContext>()
            .UseNpgsql(CreateConnectionString())
            .ReplaceService<IModelCustomizer, CoachModelCustomizer>()
            .Options;

        await using var db = new FullWorthDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(InitialMigration);

        var ids = new LegacyIds();
        await InsertRepresentativeB0DataAsync(db, ids);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.True(await db.FullWorthSpaces.AnyAsync(x => x.Id == FullWorthSpaceDefaults.LegacyId));
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.AccountOwners.CountAsync());

        Assert.True(await db.Categories.AnyAsync(x => x.Id == ids.Category));
        Assert.True(await db.BankConnections.AnyAsync(x => x.Id == ids.BankConnection));
        Assert.True(await db.Accounts.AnyAsync(x => x.Id == ids.Account));
        Assert.True(await db.BalanceSnapshots.AnyAsync(x => x.Id == ids.Balance));
        Assert.True(await db.Transactions.AnyAsync(x => x.Id == ids.Transaction));
        Assert.True(await db.Purchases.AnyAsync(x => x.Id == ids.Purchase));
        Assert.True(await db.PurchaseItems.AnyAsync(x => x.Id == ids.PurchaseItem));
        Assert.True(await db.Contracts.AnyAsync(x => x.Id == ids.Contract));
        Assert.True(await db.Budgets.AnyAsync(x => x.Id == ids.Budget));
        Assert.True(await db.Assets.AnyAsync(x => x.Id == ids.Asset));
        Assert.True(await db.Liabilities.AnyAsync(x => x.Id == ids.Liability));
        Assert.True(await db.NetWorthSnapshots.AnyAsync(x => x.Id == ids.Snapshot));
        Assert.True(await db.CategorizationRules.AnyAsync(x => x.Id == ids.Rule));

        var directSpaceIds = new[]
        {
            await db.Categories.Where(x => x.Id == ids.Category).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.BankConnections.Where(x => x.Id == ids.BankConnection).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.Accounts.Where(x => x.Id == ids.Account).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.CategorizationRules.Where(x => x.Id == ids.Rule).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.Contracts.Where(x => x.Id == ids.Contract).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.Budgets.Where(x => x.Id == ids.Budget).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.Assets.Where(x => x.Id == ids.Asset).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.Liabilities.Where(x => x.Id == ids.Liability).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.Purchases.Where(x => x.Id == ids.Purchase).Select(x => x.FullWorthSpaceId).SingleAsync(),
            await db.NetWorthSnapshots.Where(x => x.Id == ids.Snapshot).Select(x => x.FullWorthSpaceId).SingleAsync()
        };
        Assert.All(directSpaceIds, id => Assert.Equal(FullWorthSpaceDefaults.LegacyId, id));

        var account = await db.Accounts.SingleAsync(x => x.Id == ids.Account);
        var connection = await db.BankConnections.SingleAsync(x => x.Id == ids.BankConnection);
        Assert.Equal(connection.FullWorthSpaceId, account.FullWorthSpaceId);
        Assert.Equal(ids.BankConnection, account.BankConnectionId);

        Assert.Equal(ids.Category, await db.Transactions.Where(x => x.Id == ids.Transaction).Select(x => x.CategoryId).SingleAsync());
        Assert.Equal(ids.Category, await db.CategorizationRules.Where(x => x.Id == ids.Rule).Select(x => x.CategoryId).SingleAsync());
        Assert.Equal(ids.Category, await db.Contracts.Where(x => x.Id == ids.Contract).Select(x => x.CategoryId).SingleAsync());
        Assert.Equal(ids.Account, await db.Contracts.Where(x => x.Id == ids.Contract).Select(x => x.AccountId).SingleAsync());
        Assert.Equal(ids.Category, await db.Budgets.Where(x => x.Id == ids.Budget).Select(x => x.CategoryId).SingleAsync());
        Assert.Equal(ids.Transaction, await db.Purchases.Where(x => x.Id == ids.Purchase).Select(x => x.TransactionId).SingleAsync());
        Assert.Equal(ids.Purchase, await db.PurchaseItems.Where(x => x.Id == ids.PurchaseItem).Select(x => x.PurchaseId).SingleAsync());
        Assert.Equal(ids.Category, await db.PurchaseItems.Where(x => x.Id == ids.PurchaseItem).Select(x => x.CategoryId).SingleAsync());
        Assert.Null(await db.NetWorthSnapshots.Where(x => x.Id == ids.Snapshot).Select(x => x.UserId).SingleAsync());
    }

    private static async Task InsertRepresentativeB0DataAsync(FullWorthDbContext db, LegacyIds ids)
    {
        var now = DateTimeOffset.UtcNow;
        var date = new DateOnly(2026, 8, 10);

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Categories"" (""Id"", ""Key"", ""Name"", ""ParentId"", ""Icon"", ""IsSystem"", ""SortOrder"", ""CreatedAt"")
            VALUES ({ids.Category}, {"legacy-category"}, {"Legacy Category"}, NULL, NULL, TRUE, 1, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""BankConnections"" (""Id"", ""Provider"", ""InstitutionName"", ""Country"", ""AuthorizationState"", ""AuthorizationId"", ""ProviderSessionId"", ""Status"", ""ValidUntil"", ""LastAttemptAt"", ""LastSyncedAt"", ""NextSyncAllowedAt"", ""ConsecutiveFailures"", ""LastError"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.BankConnection}, {"enable-banking"}, {"Legacy Bank"}, {"DE"}, NULL, NULL, {"legacy-session"}, {"AUTHORIZED"}, NULL, NULL, {now}, NULL, 0, NULL, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Accounts"" (""Id"", ""BankConnectionId"", ""Provider"", ""IdentificationHash"", ""ProviderAccountId"", ""InstitutionName"", ""DisplayName"", ""Product"", ""AccountType"", ""Currency"", ""IbanLast4"", ""IsActive"", ""IncludeInNetWorth"", ""SortOrder"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.Account}, {ids.BankConnection}, {"enable-banking"}, {"legacy-hash"}, {"legacy-provider-account"}, {"Legacy Bank"}, {"Legacy Account"}, NULL, NULL, {"EUR"}, {"1234"}, TRUE, TRUE, 0, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""BalanceSnapshots"" (""Id"", ""AccountId"", ""Amount"", ""Currency"", ""BalanceType"", ""ReferenceDate"", ""CapturedAt"")
            VALUES ({ids.Balance}, {ids.Account}, {1234.56m}, {"EUR"}, {"CLBD"}, {date}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Transactions"" (""Id"", ""AccountId"", ""CategoryId"", ""ExternalKey"", ""ProviderTransactionId"", ""Status"", ""BookingDate"", ""ValueDate"", ""Amount"", ""Currency"", ""Counterparty"", ""NormalizedCounterparty"", ""Description"", ""MerchantCategoryCode"", ""EntryReference"", ""UserNote"", ""IsIgnored"", ""IsTransfer"", ""CategorizationSource"", ""RawJson"", ""FirstSeenAt"", ""UpdatedAt"")
            VALUES ({ids.Transaction}, {ids.Account}, {ids.Category}, {"legacy-tx"}, NULL, {"BOOK"}, {date}, {date}, {-42.50m}, {"EUR"}, {"Merchant"}, {"MERCHANT"}, {"Legacy purchase"}, NULL, NULL, NULL, FALSE, FALSE, {"manual"}, '{{}}'::jsonb, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Purchases"" (""Id"", ""TransactionId"", ""Source"", ""Merchant"", ""ExternalOrderId"", ""PurchaseDate"", ""TotalAmount"", ""Currency"", ""Status"", ""MatchConfidence"", ""ReceiptImagePath"", ""SourceReference"", ""Notes"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.Purchase}, {ids.Transaction}, {"amazon"}, {"Amazon"}, {"legacy-order"}, {date}, {42.50m}, {"EUR"}, {"confirmed"}, {0.99m}, NULL, NULL, NULL, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""PurchaseItems"" (""Id"", ""PurchaseId"", ""CategoryId"", ""Name"", ""Brand"", ""Sku"", ""Asin"", ""Quantity"", ""UnitPrice"", ""TotalPrice"", ""Currency"", ""CategorizationSource"", ""Notes"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.PurchaseItem}, {ids.Purchase}, {ids.Category}, {"Legacy Item"}, NULL, NULL, NULL, {1m}, {42.50m}, {42.50m}, {"EUR"}, {"manual"}, NULL, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Contracts"" (""Id"", ""Name"", ""ProviderName"", ""Kind"", ""CategoryId"", ""AccountId"", ""Amount"", ""Currency"", ""BillingCycle"", ""Interval"", ""StartDate"", ""EndDate"", ""NextDueDate"", ""AutoDetected"", ""IsActive"", ""Notes"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.Contract}, {"Legacy Contract"}, {"Provider"}, {"contract"}, {ids.Category}, {ids.Account}, {9.99m}, {"EUR"}, {"monthly"}, 1, {date}, NULL, {date.AddMonths(1)}, FALSE, TRUE, NULL, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Budgets"" (""Id"", ""Name"", ""CategoryId"", ""Amount"", ""Currency"", ""Period"", ""CarryOver"", ""IsActive"", ""StartDate"", ""EndDate"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.Budget}, {"Legacy Budget"}, {ids.Category}, {500m}, {"EUR"}, {"monthly"}, FALSE, TRUE, {date}, NULL, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Assets"" (""Id"", ""Name"", ""Kind"", ""CurrentValue"", ""Currency"", ""ValuedAt"", ""AnnualGrowthRate"", ""IncludeInNetWorth"", ""Notes"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.Asset}, {"Legacy Asset"}, {"other"}, {1000m}, {"EUR"}, {date}, NULL, TRUE, NULL, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Liabilities"" (""Id"", ""Name"", ""Kind"", ""CurrentBalance"", ""Currency"", ""InterestRate"", ""RegularPayment"", ""PaymentCycle"", ""NextDueDate"", ""EndDate"", ""IncludeInNetWorth"", ""Notes"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.Liability}, {"Legacy Liability"}, {"loan"}, {250m}, {"EUR"}, NULL, {25m}, {"monthly"}, {date.AddMonths(1)}, NULL, TRUE, NULL, {now}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""NetWorthSnapshots"" (""Id"", ""Date"", ""Currency"", ""Accounts"", ""Assets"", ""Liabilities"", ""NetWorth"", ""CreatedAt"")
            VALUES ({ids.Snapshot}, {date}, {"EUR"}, {1234.56m}, {1000m}, {250m}, {1984.56m}, {now});");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""CategorizationRules"" (""Id"", ""Name"", ""IsEnabled"", ""Priority"", ""Target"", ""MatchField"", ""MatchMode"", ""Pattern"", ""Direction"", ""MinAmount"", ""MaxAmount"", ""MerchantCategoryCode"", ""CategoryId"", ""MarkAsTransfer"", ""StopProcessing"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({ids.Rule}, {"Legacy Rule"}, TRUE, 100, {"transaction"}, {"combined"}, {"contains"}, {"Merchant"}, {"any"}, NULL, NULL, NULL, {ids.Category}, FALSE, TRUE, {now}, {now});");
    }

    private static string CreateConnectionString()
    {
        var server = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException("FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server.");
        return $"{server.TrimEnd(';')};Database=fullworth_upgrade_{Guid.NewGuid():N};Maximum Pool Size=50;Minimum Pool Size=0";
    }

    private sealed class LegacyIds
    {
        public Guid Category { get; } = Guid.NewGuid();
        public Guid BankConnection { get; } = Guid.NewGuid();
        public Guid Account { get; } = Guid.NewGuid();
        public Guid Balance { get; } = Guid.NewGuid();
        public Guid Transaction { get; } = Guid.NewGuid();
        public Guid Purchase { get; } = Guid.NewGuid();
        public Guid PurchaseItem { get; } = Guid.NewGuid();
        public Guid Contract { get; } = Guid.NewGuid();
        public Guid Budget { get; } = Guid.NewGuid();
        public Guid Asset { get; } = Guid.NewGuid();
        public Guid Liability { get; } = Guid.NewGuid();
        public Guid Snapshot { get; } = Guid.NewGuid();
        public Guid Rule { get; } = Guid.NewGuid();
    }
}
