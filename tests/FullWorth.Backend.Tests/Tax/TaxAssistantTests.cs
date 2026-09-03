using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Tax;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Tax;

public sealed class TaxAssistantTests
{
    [Fact]
    public async Task DisabledAssistantDoesNotAnalyzeTransactions()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId, _) = await SeedSpaceWithAdobeExpenseAsync(db);
        db.TaxSettings.Add(new TaxSettings { FullWorthSpaceId = spaceId, Enabled = false });
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var service = new TaxAnalysisService(db, store);

        var result = await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Enabled);
        Assert.Equal(0, result.SourcesAnalyzed);
        Assert.Empty(await db.TaxCandidates.ToListAsync());
        Assert.Empty(await db.TaxAnalysisRuns.ToListAsync());
    }

    [Fact]
    public async Task PersonalOptOutDoesNotAnalyzeEvenWhenSpaceFeatureIsEnabled()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId, _) = await SeedSpaceWithAdobeExpenseAsync(db);

        var store = new TaxStore(db);
        var profile = await store.UpdatePersonalProfileSettingsAsync(
            userId,
            spaceId,
            new TaxProfileSettingsUpdateRequest(false),
            CancellationToken.None);
        Assert.NotNull(profile);
        Assert.False(profile.AssistantEnabled);

        var service = new TaxAnalysisService(db, store);
        var result = await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Enabled);
        Assert.Empty(await db.TaxCandidates.ToListAsync());
        Assert.Empty(await db.TaxAnalysisRuns.ToListAsync());
    }

    [Fact]
    public async Task AdobeExpenseCreatesReviewSuggestionButNeverAutoConfirms()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId, transactionId) = await SeedSpaceWithAdobeExpenseAsync(db);

        var store = new TaxStore(db);
        var service = new TaxAnalysisService(db, store);

        var result = await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);
        var candidate = await db.TaxCandidates.SingleAsync();
        var source = await db.TaxCandidateSources.SingleAsync(x => x.IsPrimary);
        var category = await db.TaxCategories.SingleAsync(x => x.Id == candidate.TaxCategoryId);

        Assert.NotNull(result);
        Assert.True(result.Enabled);
        Assert.Equal(1, result.SourcesAnalyzed);
        Assert.Equal(1, result.CandidatesCreated);
        Assert.Equal(TaxCandidateStatuses.NeedsReview, candidate.Status);
        Assert.Equal("werbungskosten.software", category.Code);
        Assert.Equal(19.99m, candidate.GrossAmount);
        Assert.InRange(candidate.Confidence, 0.40m, 1.00m);
        Assert.Equal(TaxSourceTypes.Transaction, source.SourceType);
        Assert.Equal(transactionId, source.SourceId);
    }

    [Fact]
    public async Task MixedPurchaseFlagsOnlyMatchingItemAndSuppressesDuplicateTransactionCandidate()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId, transactionId) = await SeedSpaceWithAdobeExpenseAsync(db);

        var transaction = await db.Transactions.SingleAsync(x => x.Id == transactionId);
        transaction.Amount = -30m;
        transaction.Counterparty = "Amazon Marketplace";
        transaction.Description = "Office equipment order";
        transaction.UpdatedAt = transaction.UpdatedAt.AddMinutes(1);

        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId,
            TransactionId = transactionId,
            Source = "amazon",
            Merchant = "Amazon",
            PurchaseDate = new DateOnly(2026, 6, 1),
            TotalAmount = 30m,
            Currency = "EUR",
            CreatedByUserId = userId,
            Visibility = "space"
        };
        purchase.Items.Add(new PurchaseItem
        {
            Name = "USB-C Dock für Büro",
            RawName = "USB-C Dock für Büro",
            Quantity = 1m,
            TotalPrice = 25m,
            Currency = "EUR",
            SortOrder = 0
        });
        purchase.Items.Add(new PurchaseItem
        {
            Name = "Snacks",
            RawName = "Snacks",
            Quantity = 1m,
            TotalPrice = 5m,
            Currency = "EUR",
            SortOrder = 1
        });
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var service = new TaxAnalysisService(db, store);
        var result = await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Enabled);
        Assert.Equal(2, result.SourcesAnalyzed);
        Assert.Equal(1, result.CandidatesCreated);

        var candidate = await db.TaxCandidates.SingleAsync();
        var category = await db.TaxCategories.SingleAsync(x => x.Id == candidate.TaxCategoryId);
        var sources = await db.TaxCandidateSources.Where(x => x.TaxCandidateId == candidate.Id).ToListAsync();

        Assert.Equal(25m, candidate.GrossAmount);
        Assert.Equal("werbungskosten.arbeitsmittel", category.Code);
        Assert.Contains(sources, x => x.IsPrimary && x.SourceType == TaxSourceTypes.PurchaseItem && x.SourceId == purchase.Items.First().Id);
        Assert.Contains(sources, x => !x.IsPrimary && x.SourceType == TaxSourceTypes.Purchase && x.SourceId == purchase.Id);
        Assert.DoesNotContain(sources, x => x.SourceType == TaxSourceTypes.Transaction && x.SourceId == transactionId);
    }

    [Fact]
    public async Task ConfirmedDecisionLearnsExactSignatureAndPercentageForNextOccurrence()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId, transactionId) = await SeedSpaceWithAdobeExpenseAsync(db);
        var firstTransaction = await db.Transactions.SingleAsync(x => x.Id == transactionId);
        firstTransaction.Description = "Creative software subscription 1001";
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var service = new TaxAnalysisService(db, store);
        await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        var firstCandidate = await db.TaxCandidates.SingleAsync();
        var update = await store.UpdateCandidateAsync(
            userId,
            spaceId,
            firstCandidate.Id,
            new TaxCandidateUpdateRequest(null, 50m, TaxCandidateStatuses.Confirmed),
            CancellationToken.None);
        Assert.True(update.Found);
        var learned = await db.TaxUserMappings.SingleAsync();
        Assert.Equal("suggest", learned.Action);
        Assert.Equal(50m, learned.EligiblePercentage);

        var secondTransaction = new FinanceTransaction
        {
            AccountId = firstTransaction.AccountId,
            ExternalKey = $"tax-test:{Guid.NewGuid():N}",
            BookingDate = new DateOnly(2026, 7, 1),
            Amount = -30m,
            Currency = "EUR",
            Counterparty = "Adobe Systems",
            Description = "Creative software subscription 2002"
        };
        db.Transactions.Add(secondTransaction);
        await db.SaveChangesAsync();

        var result = await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);
        var secondCandidate = await (
            from source in db.TaxCandidateSources
            join candidate in db.TaxCandidates on source.TaxCandidateId equals candidate.Id
            where source.IsPrimary && source.SourceId == secondTransaction.Id
            select candidate).SingleAsync();

        Assert.NotNull(result);
        Assert.Equal(TaxDetectionSources.UserMapping, secondCandidate.DetectionSource);
        Assert.Equal(0.95m, secondCandidate.Confidence);
        Assert.Equal(50m, secondCandidate.EligiblePercentage);
        Assert.Equal(15m, secondCandidate.EligibleAmount);
        Assert.Equal(firstCandidate.TaxCategoryId, secondCandidate.TaxCategoryId);
        Assert.Equal(TaxCandidateStatuses.NeedsReview, secondCandidate.Status);
    }

    [Fact]
    public async Task RejectedDecisionSuppressesSameLearnedSignatureOnNextOccurrence()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId, transactionId) = await SeedSpaceWithAdobeExpenseAsync(db);
        var firstTransaction = await db.Transactions.SingleAsync(x => x.Id == transactionId);
        firstTransaction.Description = "Creative software subscription 1001";
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var service = new TaxAnalysisService(db, store);
        await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        var firstCandidate = await db.TaxCandidates.SingleAsync();
        var update = await store.UpdateCandidateAsync(
            userId,
            spaceId,
            firstCandidate.Id,
            new TaxCandidateUpdateRequest(null, null, TaxCandidateStatuses.Rejected),
            CancellationToken.None);
        Assert.True(update.Found);
        var learned = await db.TaxUserMappings.SingleAsync();
        Assert.Equal("ignore", learned.Action);

        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = firstTransaction.AccountId,
            ExternalKey = $"tax-test:{Guid.NewGuid():N}",
            BookingDate = new DateOnly(2026, 7, 1),
            Amount = -25m,
            Currency = "EUR",
            Counterparty = "Adobe Systems",
            Description = "Creative software subscription 2002"
        });
        await db.SaveChangesAsync();

        var result = await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(await db.TaxCandidates.ToListAsync());
        Assert.Equal(TaxCandidateStatuses.Rejected, (await db.TaxCandidates.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReanalysisDoesNotOverwriteConfirmedDecision()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId, transactionId) = await SeedSpaceWithAdobeExpenseAsync(db);

        var store = new TaxStore(db);
        var service = new TaxAnalysisService(db, store);
        await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        var candidate = await db.TaxCandidates.SingleAsync();
        var update = await store.UpdateCandidateAsync(
            userId,
            spaceId,
            candidate.Id,
            new TaxCandidateUpdateRequest(null, 50m, TaxCandidateStatuses.Confirmed),
            CancellationToken.None);
        Assert.True(update.Found);

        var transaction = await db.Transactions.SingleAsync(x => x.Id == transactionId);
        transaction.Description = "Adobe software renewal changed";
        transaction.UpdatedAt = transaction.UpdatedAt.AddMinutes(1);
        await db.SaveChangesAsync();

        var second = await service.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);
        var after = await db.TaxCandidates.SingleAsync();

        Assert.NotNull(second);
        Assert.Equal(0, second.CandidatesChanged);
        Assert.Equal(TaxCandidateStatuses.Confirmed, after.Status);
        Assert.Equal(50m, after.EligiblePercentage);
        Assert.Equal(10.00m, after.EligibleAmount);
        Assert.NotNull(after.ReviewedAt);
        Assert.Equal(userId, after.ReviewedByUserId);
    }

    private static async Task<(Guid UserId, Guid SpaceId, Guid TransactionId)> SeedSpaceWithAdobeExpenseAsync(FullWorth.Backend.Data.FullWorthDbContext db)
    {
        var user = new FullWorthUser
        {
            EmailNormalized = $"tax-{Guid.NewGuid():N}@example.test",
            DisplayName = "Tax Test"
        };
        var space = new FullWorthSpace { Name = "Tax Test Space", BaseCurrency = "EUR" };
        db.Users.Add(user);
        db.FullWorthSpaces.Add(space);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = space.Id,
            UserId = user.Id,
            Role = FullWorthSpaceRoles.Owner
        });

        var account = new FinanceAccount
        {
            FullWorthSpaceId = space.Id,
            Provider = "manual",
            IdentificationHash = $"manual:{Guid.NewGuid():N}",
            ProviderAccountId = $"manual:{Guid.NewGuid():N}",
            InstitutionName = "Test Bank",
            DisplayName = "Girokonto",
            Currency = "EUR"
        };
        db.Accounts.Add(account);
        db.AccountOwners.Add(new AccountOwner
        {
            AccountId = account.Id,
            UserId = user.Id,
            OwnershipType = AccountOwnershipTypes.Owner
        });

        var transaction = new FinanceTransaction
        {
            AccountId = account.Id,
            ExternalKey = $"tax-test:{Guid.NewGuid():N}",
            BookingDate = new DateOnly(2026, 6, 1),
            Amount = -19.99m,
            Currency = "EUR",
            Counterparty = "Adobe Systems",
            Description = "Creative software subscription"
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return (user.Id, space.Id, transaction.Id);
    }
}
