using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Purchases;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class IntelligenceDomainSuggestionReviewTests
{
    [Fact]
    public async Task Accepting_product_proposal_marks_review_only_and_keeps_item_unchanged()
    {
        await using var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
        await using var financeConnection = new SqliteConnection("Data Source=:memory:");
        await intelligenceConnection.OpenAsync();
        await financeConnection.OpenAsync();

        await using var intelligenceDb = new IntelligenceDbContext(
            new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(intelligenceConnection).Options);
        await using var financeDb = new FullWorthDbContext(
            new DbContextOptionsBuilder<FullWorthDbContext>().UseSqlite(financeConnection).Options);
        await intelligenceDb.Database.EnsureCreatedAsync();
        await financeDb.Database.EnsureCreatedAsync();

        var space = new FullWorthSpace { Name = "Review", BaseCurrency = "EUR" };
        var purchase = new Purchase
        {
            FullWorthSpaceId = space.Id,
            Merchant = "REWE",
            PurchaseDate = new DateOnly(2026, 9, 1),
            TotalAmount = 1.49m,
            Currency = "EUR"
        };
        var item = new PurchaseItem
        {
            Purchase = purchase,
            PurchaseId = purchase.Id,
            RawName = "COLA ZERO",
            Name = "Cola Zero",
            TotalPrice = 1.49m,
            Currency = "EUR"
        };
        purchase.Items.Add(item);
        financeDb.FullWorthSpaces.Add(space);
        financeDb.Purchases.Add(purchase);
        await financeDb.SaveChangesAsync();

        var suggestion = new IntelligenceSuggestion
        {
            FullWorthSpaceId = space.Id,
            Type = "product-normalization",
            SubjectType = "purchase-item",
            SubjectId = item.Id.ToString("N"),
            SemanticKey = "product-normalization:v1",
            ProposedPayloadJson = "{\"canonicalName\":\"Coca-Cola Zero 1.5L\",\"categoryKey\":\"food.groceries\"}",
            EvidenceJson = "{}",
            Provider = "fake",
            Model = "fake",
            Confidence = .9m
        };
        intelligenceDb.IntelligenceSuggestions.Add(suggestion);
        await intelligenceDb.SaveChangesAsync();
        var actor = Guid.NewGuid();

        var result = await new IntelligenceSuggestionReviewService(intelligenceDb, financeDb)
            .AcceptAsync(suggestion.Id, actor, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(IntelligenceSuggestionStatuses.Accepted,
            (await intelligenceDb.IntelligenceSuggestions.AsNoTracking().SingleAsync()).Status);
        var feedback = await intelligenceDb.IntelligenceFeedbackEvents.AsNoTracking().SingleAsync();
        Assert.Equal("ai_suggestion_accepted", feedback.EventType);
        Assert.Equal("purchase-item", feedback.SubjectType);
        Assert.False(feedback.CloudEligible);

        var unchanged = await financeDb.PurchaseItems.AsNoTracking().SingleAsync(x => x.Id == item.Id);
        Assert.Equal("Cola Zero", unchanged.Name);
        Assert.Null(unchanged.ProductId);
        Assert.Null(unchanged.CategoryId);
    }

    [Fact]
    public async Task Accepting_stale_product_proposal_is_rejected()
    {
        await using var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
        await using var financeConnection = new SqliteConnection("Data Source=:memory:");
        await intelligenceConnection.OpenAsync();
        await financeConnection.OpenAsync();

        await using var intelligenceDb = new IntelligenceDbContext(
            new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(intelligenceConnection).Options);
        await using var financeDb = new FullWorthDbContext(
            new DbContextOptionsBuilder<FullWorthDbContext>().UseSqlite(financeConnection).Options);
        await intelligenceDb.Database.EnsureCreatedAsync();
        await financeDb.Database.EnsureCreatedAsync();

        var space = new FullWorthSpace { Name = "Review", BaseCurrency = "EUR" };
        financeDb.FullWorthSpaces.Add(space);
        await financeDb.SaveChangesAsync();

        var suggestion = new IntelligenceSuggestion
        {
            FullWorthSpaceId = space.Id,
            Type = "product-normalization",
            SubjectType = "purchase-item",
            SubjectId = Guid.NewGuid().ToString("N"),
            SemanticKey = "product-normalization:v1",
            ProposedPayloadJson = "{}",
            EvidenceJson = "{}",
            Provider = "fake",
            Model = "fake",
            Confidence = .65m
        };
        intelligenceDb.IntelligenceSuggestions.Add(suggestion);
        await intelligenceDb.SaveChangesAsync();

        var result = await new IntelligenceSuggestionReviewService(intelligenceDb, financeDb)
            .AcceptAsync(suggestion.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("subject_not_found", result.ErrorCode);
        Assert.Equal(IntelligenceSuggestionStatuses.Pending,
            (await intelligenceDb.IntelligenceSuggestions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await intelligenceDb.IntelligenceFeedbackEvents.AsNoTracking().ToListAsync());
    }
}
