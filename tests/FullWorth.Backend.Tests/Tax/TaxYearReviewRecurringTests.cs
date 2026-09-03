using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Tax;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Tax;

public sealed class TaxYearReviewRecurringTests
{
    [Fact]
    public async Task YearReview_FlagsMonthlyGapAndUnusualValueOncePerRecurringCase()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var (userId, spaceId) = await SeedSpaceAsync(db);

        db.TaxSettings.Add(new TaxSettings
        {
            FullWorthSpaceId = spaceId,
            Enabled = true,
            AnalyzeTransactions = false,
            AnalyzePurchases = true,
            AnalyzeDocuments = false,
            AutomaticAnalysisEnabled = false,
            DefaultTaxYear = 2026,
            CountryCode = "DE"
        });

        AddPurchase(db, userId, spaceId, new DateOnly(2026, 1, 5), 100m);
        AddPurchase(db, userId, spaceId, new DateOnly(2026, 2, 5), 100m);
        AddPurchase(db, userId, spaceId, new DateOnly(2026, 3, 5), 100m);
        AddPurchase(db, userId, spaceId, new DateOnly(2026, 5, 5), 180m);
        await db.SaveChangesAsync();

        var store = new TaxStore(db);
        var coordinator = new TaxAnalysisCoordinator(db, store, new TaxAnalysisService(db, store));
        await coordinator.AnalyzeAsync(userId, spaceId, 2026, CancellationToken.None);

        var candidateIds = await db.TaxCandidates.Select(x => x.Id).ToListAsync();
        Assert.Equal(4, candidateIds.Count);
        foreach (var candidateId in candidateIds)
        {
            var result = await store.UpdateCandidateAsync(
                userId,
                spaceId,
                candidateId,
                new TaxCandidateUpdateRequest(null, null, TaxCandidateStatuses.Confirmed),
                CancellationToken.None);
            Assert.True(result.Found);
        }

        var review = await new TaxYearReviewService(db, store)
            .BuildAsync(userId, spaceId, 2026, CancellationToken.None);

        Assert.NotNull(review);
        Assert.Equal(0, review.OpenReviewCount);
        var gap = Assert.Single(review.Checks.Where(x => x.Code == "recurring_gap"));
        var value = Assert.Single(review.Checks.Where(x => x.Code == "recurring_value_change"));
        Assert.Equal(1, gap.Count);
        Assert.Equal(1, value.Count);
        Assert.False(review.Ready);
    }

    private static void AddPurchase(FullWorthDbContext db, Guid userId, Guid spaceId, DateOnly date, decimal amount)
    {
        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId,
            Source = "manual",
            Merchant = "Recurring Office Service",
            PurchaseDate = date,
            TotalAmount = amount,
            Currency = "EUR",
            CreatedByUserId = userId,
            Visibility = "space"
        };
        purchase.Items.Add(new PurchaseItem
        {
            Name = "Office Software Abo",
            RawName = "Office Software Abo",
            Quantity = 1m,
            TotalPrice = amount,
            Currency = "EUR"
        });
        db.Purchases.Add(purchase);
    }

    private static async Task<(Guid UserId, Guid SpaceId)> SeedSpaceAsync(FullWorthDbContext db)
    {
        var user = new FullWorthUser
        {
            EmailNormalized = $"tax-recurring-{Guid.NewGuid():N}@example.test",
            DisplayName = "Tax Recurring"
        };
        var space = new FullWorthSpace { Name = "Tax Recurring Space", BaseCurrency = "EUR" };
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
}
