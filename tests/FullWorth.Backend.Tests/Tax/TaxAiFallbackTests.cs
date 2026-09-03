using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Tax;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Tax;

public sealed class TaxAiFallbackTests
{
    [Fact]
    public async Task ThrowingAiProvider_DoesNotBreakDeterministicSuggestion()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId) = await SeedSpaceAsync(db);

        db.TaxSettings.Add(new TaxSettings
        {
            FullWorthSpaceId = spaceId,
            Enabled = true,
            AiAnalysisEnabled = true,
            AnalyzeTransactions = false,
            AnalyzePurchases = true,
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
            PurchaseDate = new DateOnly(2026, 6, 1),
            TotalAmount = 120m,
            Currency = "EUR",
            CreatedByUserId = userId,
            Visibility = "space"
        };
        purchase.Items.Add(new PurchaseItem
        {
            Name = "Monitor fürs Büro",
            RawName = "Monitor fürs Büro",
            Quantity = 1m,
            TotalPrice = 120m,
            Currency = "EUR"
        });
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var coordinator = new TaxAnalysisCoordinator(
            db,
            store,
            new TaxAnalysisService(db, store),
            new ThrowingAiResolver());

        var result = await coordinator.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Enabled);
        var candidate = await db.TaxCandidates.SingleAsync();
        Assert.Equal(TaxCandidateStatuses.NeedsReview, candidate.Status);
        Assert.Equal(TaxDetectionSources.Keyword, candidate.DetectionSource);
        Assert.DoesNotContain("KI-Hinweis", candidate.Explanation);
    }

    private static async Task<(Guid UserId, Guid SpaceId)> SeedSpaceAsync(FullWorthDbContext db)
    {
        var user = new FullWorthUser
        {
            EmailNormalized = $"tax-ai-fallback-{Guid.NewGuid():N}@example.test",
            DisplayName = "Tax AI Fallback"
        };
        var space = new FullWorthSpace { Name = "Tax AI Fallback Space", BaseCurrency = "EUR" };
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

    private sealed class ThrowingAiResolver : ITaxAiResolver
    {
        public Task<TaxAiCandidateSuggestion?> SuggestAsync(TaxAiCandidateContext context, CancellationToken ct) =>
            throw new InvalidOperationException("provider unavailable");
    }
}
