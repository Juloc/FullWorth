using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Ingestion;
using Microsoft.EntityFrameworkCore;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Ingestion;

public sealed class IngestionBaselineTests
{
    [Fact]
    public async Task RepeatedTransactionUsesAccountIdAndExternalKeyForDeduplication()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(CreateBatch("enable-banking", "session-1", "account-hash", "account-a", "Main", "tx-1", -12.34m), CancellationToken.None);
        await service.IngestAsync(CreateBatch("enable-banking", "session-1", "account-hash", "account-a", "Main", "tx-1", -15.67m), CancellationToken.None);

        var transactions = await db.Transactions.AsNoTracking().ToListAsync();
        var transaction = Assert.Single(transactions);
        Assert.Equal(-15.67m, transaction.Amount);
        Assert.Equal("tx-1", transaction.ExternalKey);

        var accountId = transaction.AccountId;
        await service.IngestAsync(CreateAccountOnlyBatch("enable-banking", "session-2", "other-account-hash", "account-b", "Other"), CancellationToken.None);
        var secondAccountId = await db.Accounts.AsNoTracking()
            .Where(x => x.IdentificationHash == "other-account-hash")
            .Select(x => x.Id)
            .SingleAsync();
        db.Transactions.Add(new FullWorth.Backend.Modules.Transactions.FinanceTransaction
        {
            AccountId = secondAccountId,
            ExternalKey = "tx-1",
            Amount = 1m,
            Currency = "EUR"
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Transactions.CountAsync(x => x.ExternalKey == "tx-1"));
        Assert.Single(await db.Transactions.Where(x => x.AccountId == accountId && x.ExternalKey == "tx-1").ToListAsync());
    }

    [Fact]
    public async Task TransactionMoneyRemainsExactDecimal()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);
        const decimal amount = -1234567890.12345678m;

        await service.IngestAsync(CreateBatch("enable-banking", "session-money", "money-hash", "money-account", "Money", "money-tx", amount), CancellationToken.None);

        db.ChangeTracker.Clear();
        var stored = await db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal(typeof(decimal), stored.GetType().GetProperty(nameof(stored.Amount))!.PropertyType);
        Assert.Equal(amount, stored.Amount);
    }

    [Fact]
    public async Task AccountReconciliationUsesProviderAndIdentificationHashWithoutOverwritingLocalName()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(CreateAccountOnlyBatch("enable-banking", "session-a", "same-hash", "provider-account-1", "Old provider name"), CancellationToken.None);
        var locallyRenamed = await db.Accounts.SingleAsync(x => x.Provider == "enable-banking" && x.IdentificationHash == "same-hash");
        locallyRenamed.DisplayName = "Gehalt";
        await db.SaveChangesAsync();

        await service.IngestAsync(CreateAccountOnlyBatch("enable-banking", "session-a", "same-hash", "provider-account-2", "Updated provider name"), CancellationToken.None);
        await service.IngestAsync(CreateAccountOnlyBatch("other-provider", "session-b", "same-hash", "provider-account-3", "Other provider"), CancellationToken.None);

        var accounts = await db.Accounts.AsNoTracking().OrderBy(x => x.Provider).ToListAsync();
        Assert.Equal(2, accounts.Count);

        var enableBanking = Assert.Single(accounts.Where(x => x.Provider == "enable-banking"));
        Assert.Equal("provider-account-2", enableBanking.ProviderAccountId);
        Assert.Equal("Gehalt", enableBanking.DisplayName);

        var otherProvider = Assert.Single(accounts.Where(x => x.Provider == "other-provider"));
        Assert.Equal(enableBanking.IdentificationHash, otherProvider.IdentificationHash);
        Assert.NotEqual(enableBanking.Id, otherProvider.Id);
    }

    [Fact]
    public async Task ProviderPlaceholderNameCanBeReplacedWhenRealDetailsArrive()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(new FinanceIngestBatch(
            new BankConnectionBatch(null, "enable-banking", "Mock ASPSP", "DE", "session-a", "AUTHORIZED", null, DateTimeOffset.UtcNow, null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem("hash-placeholder", "provider-1", "Mock ASPSP", "Mock ASPSP", null, null, "EUR", null, true, HasDetails: false)],
            [],
            []), CancellationToken.None);

        await service.IngestAsync(new FinanceIngestBatch(
            new BankConnectionBatch(null, "enable-banking", "Mock ASPSP", "DE", "session-a", "AUTHORIZED", null, DateTimeOffset.UtcNow, null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem("hash-placeholder", "provider-2", "Mock ASPSP", "Tagesgeld", "Savings", "savings", "EUR", "1234", true, HasDetails: true)],
            [],
            []), CancellationToken.None);

        db.ChangeTracker.Clear();
        var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.IdentificationHash == "hash-placeholder");
        Assert.Equal("Tagesgeld", account.DisplayName);
        Assert.Equal("provider-2", account.ProviderAccountId);
    }

    [Fact]
    public async Task ChangedPrimaryIdentificationHashReusesAccountThroughAlias()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(new FinanceIngestBatch(
            new BankConnectionBatch(null, "enable-banking", "Test Bank", "DE", "session-a", "AUTHORIZED", null, DateTimeOffset.UtcNow, null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem(
                "old-hash", "provider-1", "Test Bank", "Girokonto", "Current", "checking", "EUR", "1234", true,
                IdentificationHashes: ["old-hash"])],
            [],
            []), CancellationToken.None);

        var originalId = await db.Accounts.AsNoTracking().Select(x => x.Id).SingleAsync();

        await service.IngestAsync(new FinanceIngestBatch(
            new BankConnectionBatch(null, "enable-banking", "Test Bank", "DE", "session-a", "AUTHORIZED", null, DateTimeOffset.UtcNow, null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem(
                "new-hash", "provider-2", "Test Bank", "Girokonto", "Current", "checking", "EUR", "1234", true,
                IdentificationHashes: ["new-hash", "old-hash"])],
            [],
            []), CancellationToken.None);

        db.ChangeTracker.Clear();
        var account = Assert.Single(await db.Accounts.AsNoTracking().ToListAsync());
        Assert.Equal(originalId, account.Id);
        Assert.Equal("new-hash", account.IdentificationHash);
        Assert.Equal("provider-2", account.ProviderAccountId);
        Assert.Contains("old-hash", account.IdentificationHashesJson);
        Assert.Contains("new-hash", account.IdentificationHashesJson);
    }

    [Fact]
    public async Task TransactionRuleMatchingAssignsCategoryDeterministically()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var category = new FinanceCategory
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            Key = "groceries-test",
            Name = "Groceries test"
        };
        db.Categories.Add(category);
        db.CategorizationRules.Add(new CategorizationRule
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            Name = "REWE expense",
            Target = "transaction",
            MatchField = "normalized_counterparty",
            MatchMode = "contains",
            Pattern = "REWE",
            Direction = "expense",
            MinAmount = 10m,
            MaxAmount = 20m,
            CategoryId = category.Id,
            Priority = 10,
            StopProcessing = true
        });
        await db.SaveChangesAsync();

        var service = new IngestionService(db);
        var batch = CreateBatch("enable-banking", "session-rule", "rule-hash", "rule-account", "Rules", "rule-tx", -12.34m, "Rewe Markt 42");
        await service.IngestAsync(batch, CancellationToken.None);

        var transaction = await db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal(category.Id, transaction.CategoryId);
        Assert.Equal("rule", transaction.CategorizationSource);
    }

    private static FinanceIngestBatch CreateBatch(
        string provider,
        string sessionId,
        string identificationHash,
        string providerAccountId,
        string displayName,
        string externalKey,
        decimal amount,
        string? counterparty = "Example Merchant")
    {
        return new FinanceIngestBatch(
            new BankConnectionBatch(null, provider, "Test Bank", "DE", sessionId, "AUTHORIZED", null, new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero), null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem(identificationHash, providerAccountId, "Test Bank", displayName, "Current", "checking", "EUR", "1234", true)],
            [],
            [new TransactionBatchItem(identificationHash, externalKey, "provider-tx", "BOOK", new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 10), amount, "EUR", counterparty, "Card payment", null, null, "{\"provider\":\"test\"}")]);
    }

    [Fact]
    public async Task PlaceholderMetadataSeedsNewAccountsButNeverOverwritesDetailedOnes()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        // A detailed sync stored the real name/currency/product; a later details-less sync (the
        // provider's session payload was sparse and the details resource 404ed) must not clobber it.
        await service.IngestAsync(CreateAccountOnlyBatch("enable-banking", "session-a", "hash-a", "provider-1", "Girokonto"), CancellationToken.None);
        await service.IngestAsync(new FinanceIngestBatch(
            new BankConnectionBatch(null, "enable-banking", "Test Bank", "DE", "session-a", "AUTHORIZED", null, DateTimeOffset.UtcNow, null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem("hash-a", "provider-2", "Test Bank", "Test Bank", null, null, "USD", null, true, HasDetails: false)],
            [],
            []), CancellationToken.None);

        db.ChangeTracker.Clear();
        var detailed = await db.Accounts.AsNoTracking().SingleAsync(x => x.IdentificationHash == "hash-a");
        Assert.Equal("Girokonto", detailed.DisplayName);
        Assert.Equal("Current", detailed.Product);
        Assert.Equal("EUR", detailed.Currency);
        Assert.Equal("1234", detailed.IbanLast4);
        // Non-metadata reconciliation still applies.
        Assert.Equal("provider-2", detailed.ProviderAccountId);

        // For a brand-new account the placeholder metadata is the best available and is applied.
        await service.IngestAsync(new FinanceIngestBatch(
            new BankConnectionBatch(null, "enable-banking", "Test Bank", "DE", "session-a", "AUTHORIZED", null, DateTimeOffset.UtcNow, null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem("hash-new", "provider-9", "Test Bank", "Test Bank", null, null, "EUR", null, true, HasDetails: false)],
            [],
            []), CancellationToken.None);
        var seeded = await db.Accounts.AsNoTracking().SingleAsync(x => x.IdentificationHash == "hash-new");
        Assert.Equal("Test Bank", seeded.DisplayName);
    }

    private static FinanceIngestBatch CreateAccountOnlyBatch(
        string provider,
        string sessionId,
        string identificationHash,
        string providerAccountId,
        string displayName)
    {
        return new FinanceIngestBatch(
            new BankConnectionBatch(null, provider, "Test Bank", "DE", sessionId, "AUTHORIZED", null, new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero), null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem(identificationHash, providerAccountId, "Test Bank", displayName, "Current", "checking", "EUR", "1234", true)],
            [],
            []);
    }
}
