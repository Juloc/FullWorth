using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Tax;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Tax;

public sealed class TaxCompletionTests
{
    [Fact]
    public async Task MissingPurchaseReceiptIsSurfacedAndCompletedEvidenceCanMakeYearReady()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId) = await SeedSpaceAsync(db);

        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId,
            Source = "manual",
            Merchant = "Office Shop",
            PurchaseDate = new DateOnly(2026, 5, 12),
            TotalAmount = 89m,
            Currency = "EUR",
            CreatedByUserId = userId,
            Visibility = "space"
        };
        purchase.Items.Add(new PurchaseItem
        {
            Name = "USB-C Docking Station fürs Büro",
            RawName = "USB-C Docking Station fürs Büro",
            Quantity = 1m,
            TotalPrice = 89m,
            Currency = "EUR"
        });
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var deterministic = new TaxAnalysisService(db, store);
        var coordinator = new TaxAnalysisCoordinator(db, store, deterministic);

        await coordinator.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);
        var candidate = await db.TaxCandidates.SingleAsync();
        Assert.Equal(TaxCandidateStatuses.NeedsDocument, candidate.Status);

        var firstReview = await new TaxYearReviewService(db, store)
            .BuildAsync(userId, spaceId, 2026, CancellationToken.None);
        Assert.NotNull(firstReview);
        Assert.False(firstReview.Ready);
        Assert.Equal(1, firstReview.MissingDocumentCount);

        db.PurchaseDocuments.Add(new PurchaseDocument
        {
            PurchaseId = purchase.Id,
            DocumentType = "receipt",
            OriginalFileName = "receipt.pdf",
            MediaType = "application/pdf",
            StoragePath = "test/receipt.pdf",
            Sha256 = "completion-test",
            SizeBytes = 1,
            Status = "uploaded"
        });
        await db.SaveChangesAsync();

        await coordinator.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);
        candidate = await db.TaxCandidates.SingleAsync();
        Assert.Equal(TaxCandidateStatuses.NeedsReview, candidate.Status);

        var update = await store.UpdateCandidateAsync(
            userId,
            spaceId,
            candidate.Id,
            new TaxCandidateUpdateRequest(null, null, TaxCandidateStatuses.Confirmed),
            CancellationToken.None);
        Assert.True(update.Found);

        var finalReview = await new TaxYearReviewService(db, store)
            .BuildAsync(userId, spaceId, 2026, CancellationToken.None);
        Assert.NotNull(finalReview);
        Assert.True(finalReview.Ready);
        Assert.Equal(0, finalReview.OpenReviewCount);
        Assert.Equal(0, finalReview.MissingDocumentCount);
    }

    [Fact]
    public async Task AnalyzeDocumentsDisabledNeitherUsesNorRequiresReceipt()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId) = await SeedSpaceAsync(db);

        db.TaxSettings.Add(new TaxSettings
        {
            FullWorthSpaceId = spaceId,
            Enabled = true,
            AnalyzePurchases = true,
            AnalyzeTransactions = false,
            AnalyzeDocuments = false,
            AutomaticAnalysisEnabled = false,
            DefaultTaxYear = 2026,
            CountryCode = "DE"
        });
        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId,
            Source = "manual",
            Merchant = "Office Shop",
            PurchaseDate = new DateOnly(2026, 3, 10),
            TotalAmount = 80m,
            Currency = "EUR",
            CreatedByUserId = userId,
            Visibility = "space"
        };
        purchase.Items.Add(new PurchaseItem
        {
            Name = "Monitor fürs Büro",
            RawName = "Monitor fürs Büro",
            Quantity = 1m,
            TotalPrice = 80m,
            Currency = "EUR"
        });
        purchase.Documents.Add(new PurchaseDocument
        {
            DocumentType = "receipt",
            OriginalFileName = "ignored-receipt.pdf",
            MediaType = "application/pdf",
            StoragePath = "test/ignored-receipt.pdf",
            Sha256 = "document-opt-out",
            SizeBytes = 1,
            Status = "uploaded"
        });
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var deterministic = new TaxAnalysisService(db, store);
        var coordinator = new TaxAnalysisCoordinator(db, store, deterministic);
        await coordinator.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        var candidate = await db.TaxCandidates.SingleAsync();
        Assert.Equal(TaxCandidateStatuses.NeedsReview, candidate.Status);
        Assert.DoesNotContain("Beleg", candidate.Explanation);
        Assert.DoesNotContain(await db.TaxCandidateSources.Where(x => x.TaxCandidateId == candidate.Id).ToListAsync(),
            x => x.SourceType == TaxSourceTypes.PurchaseDocument);

        var review = await new TaxYearReviewService(db, store)
            .BuildAsync(userId, spaceId, 2026, CancellationToken.None);
        Assert.NotNull(review);
        Assert.Equal(0, review.MissingDocumentCount);
    }

    [Fact]
    public async Task AiCanOnlyRefineSuggestionAndNeverAutoConfirm()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId) = await SeedSpaceAsync(db);

        db.TaxSettings.Add(new TaxSettings
        {
            FullWorthSpaceId = spaceId,
            Enabled = true,
            AiAnalysisEnabled = true,
            AutomaticAnalysisEnabled = false,
            DefaultTaxYear = 2026,
            CountryCode = "DE"
        });
        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId,
            Source = "manual",
            Merchant = "Office Shop",
            PurchaseDate = new DateOnly(2026, 4, 2),
            TotalAmount = 100m,
            Currency = "EUR",
            CreatedByUserId = userId,
            Visibility = "space"
        };
        purchase.Items.Add(new PurchaseItem
        {
            Name = "Monitor fürs Büro",
            RawName = "Monitor fürs Büro",
            Quantity = 1m,
            TotalPrice = 100m,
            Currency = "EUR"
        });
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var deterministic = new TaxAnalysisService(db, store);
        var coordinator = new TaxAnalysisCoordinator(db, store, deterministic, new FakeAiResolver());
        await coordinator.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        var candidate = await db.TaxCandidates.SingleAsync();
        Assert.NotEqual(TaxCandidateStatuses.Confirmed, candidate.Status);
        Assert.Equal(TaxCandidateStatuses.NeedsDocument, candidate.Status);
        Assert.Equal(50m, candidate.EligiblePercentage);
        Assert.Equal(50m, candidate.EligibleAmount);
        Assert.Equal(TaxDetectionSources.Combined, candidate.DetectionSource);
        Assert.Contains("KI-Hinweis", candidate.Explanation);
    }

    [Fact]
    public async Task AutomaticAnalysisNeverInvokesAiResolver()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId) = await SeedSpaceAsync(db);

        db.TaxSettings.Add(new TaxSettings
        {
            FullWorthSpaceId = spaceId,
            Enabled = true,
            AiAnalysisEnabled = true,
            AutomaticAnalysisEnabled = true,
            AnalyzeDocuments = false,
            DefaultTaxYear = 2026,
            CountryCode = "DE"
        });
        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId,
            Source = "manual",
            Merchant = "Office Shop",
            PurchaseDate = new DateOnly(2026, 4, 2),
            TotalAmount = 100m,
            Currency = "EUR",
            CreatedByUserId = userId,
            Visibility = "space"
        };
        purchase.Items.Add(new PurchaseItem
        {
            Name = "Monitor fürs Büro",
            RawName = "Monitor fürs Büro",
            Quantity = 1m,
            TotalPrice = 100m,
            Currency = "EUR"
        });
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();

        var resolver = new CountingAiResolver();
        var store = new TaxStore(db);
        var coordinator = new TaxAnalysisCoordinator(db, store, new TaxAnalysisService(db, store), resolver);
        await coordinator.AnalyzeAsync(userId, spaceId, 2026, "automatic", CancellationToken.None);

        Assert.Equal(0, resolver.CallCount);
        var candidate = await db.TaxCandidates.SingleAsync();
        Assert.Equal(TaxCandidateStatuses.NeedsReview, candidate.Status);
        Assert.DoesNotContain("KI-Hinweis", candidate.Explanation);
        Assert.Equal("automatic", await db.TaxAnalysisRuns.Select(x => x.Trigger).SingleAsync());
    }

    private static async Task<(Guid UserId, Guid SpaceId)> SeedSpaceAsync(FullWorth.Backend.Data.FullWorthDbContext db)
    {
        var user = new FullWorthUser
        {
            EmailNormalized = $"tax-completion-{Guid.NewGuid():N}@example.test",
            DisplayName = "Tax Completion"
        };
        var space = new FullWorthSpace { Name = "Tax Completion Space", BaseCurrency = "EUR" };
        db.Users.Add(user);
        db.FullWorthSpaces.Add(space);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = space.Id,
            UserId = user.Id,
            Role = FullWorthSpaceRoles.Owner
        });
        await db.SaveChangesAsync();
        return (user.Id, space.Id);
    }

    private sealed class FakeAiResolver : ITaxAiResolver
    {
        public Task<TaxAiCandidateSuggestion?> SuggestAsync(TaxAiCandidateContext context, CancellationToken ct) =>
            Task.FromResult<TaxAiCandidateSuggestion?>(new(
                "werbungskosten.arbeitsmittel",
                0.88m,
                50m,
                "Der Fall sollte vom Nutzer als teilweise beruflich geprüft werden."));
    }

    private sealed class CountingAiResolver : ITaxAiResolver
    {
        public int CallCount { get; private set; }

        public Task<TaxAiCandidateSuggestion?> SuggestAsync(TaxAiCandidateContext context, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<TaxAiCandidateSuggestion?>(new(
                "werbungskosten.arbeitsmittel",
                0.99m,
                1m,
                "This must never be used by automatic analysis."));
        }
    }
}
