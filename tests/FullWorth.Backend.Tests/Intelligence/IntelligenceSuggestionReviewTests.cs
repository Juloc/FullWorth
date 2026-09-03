using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class IntelligenceSuggestionReviewTests
{
    [Fact]
    public async Task Accepting_merchant_category_suggestion_creates_confirmed_mapping_and_feedback()
    {
        await using var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
        await using var financeConnection = new SqliteConnection("Data Source=:memory:");
        await intelligenceConnection.OpenAsync();
        await financeConnection.OpenAsync();

        var intelligenceOptions = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(intelligenceConnection).Options;
        var financeOptions = new DbContextOptionsBuilder<FullWorthDbContext>().UseSqlite(financeConnection).Options;
        await using var intelligenceDb = new IntelligenceDbContext(intelligenceOptions);
        await using var financeDb = new FullWorthDbContext(financeOptions);
        await intelligenceDb.Database.EnsureCreatedAsync();
        await financeDb.Database.EnsureCreatedAsync();

        var space = new FullWorthSpace { Name = "Test" };
        var category = new FinanceCategory
        {
            FullWorthSpaceId = space.Id,
            Key = "food.groceries",
            Name = "Lebensmittel",
            IsSystem = true
        };
        financeDb.FullWorthSpaces.Add(space);
        financeDb.Categories.Add(category);
        await financeDb.SaveChangesAsync();

        var suggestion = new IntelligenceSuggestion
        {
            FullWorthSpaceId = space.Id,
            Type = "merchant-category",
            SubjectType = "merchant",
            SubjectId = "REWE",
            SemanticKey = "merchant-category:expense",
            ProposedPayloadJson = JsonSerializer.Serialize(new
            {
                categoryKey = category.Key,
                direction = "expense",
                evidenceSummary = "Repeated grocery merchant"
            }),
            EvidenceJson = "{}",
            Provider = "fake",
            Model = "fake-model",
            Confidence = 0.9m
        };
        intelligenceDb.IntelligenceSuggestions.Add(suggestion);
        await intelligenceDb.SaveChangesAsync();
        var actor = Guid.NewGuid();
        var service = new IntelligenceSuggestionReviewService(intelligenceDb, financeDb);

        var result = await service.AcceptAsync(suggestion.Id, actor, CancellationToken.None);

        Assert.True(result.Success);
        var mapping = await intelligenceDb.LearnedMerchantMappings.SingleAsync();
        Assert.Equal(space.Id, mapping.FullWorthSpaceId);
        Assert.Equal("REWE", mapping.NormalizedCounterparty);
        Assert.Equal("expense", mapping.Direction);
        Assert.Equal(category.Id, mapping.CategoryId);
        Assert.Equal("ai-confirmed", mapping.Source);
        Assert.Equal(actor, mapping.CreatedByUserId);
        Assert.Equal(IntelligenceSuggestionStatuses.Accepted,
            (await intelligenceDb.IntelligenceSuggestions.SingleAsync()).Status);
        var feedback = await intelligenceDb.IntelligenceFeedbackEvents.SingleAsync();
        Assert.Equal("ai_suggestion_accepted", feedback.EventType);
        Assert.False(feedback.CloudEligible);
    }

    [Fact]
    public async Task Rejecting_suggestion_records_feedback_without_creating_mapping()
    {
        await using var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
        await using var financeConnection = new SqliteConnection("Data Source=:memory:");
        await intelligenceConnection.OpenAsync();
        await financeConnection.OpenAsync();

        var intelligenceOptions = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(intelligenceConnection).Options;
        var financeOptions = new DbContextOptionsBuilder<FullWorthDbContext>().UseSqlite(financeConnection).Options;
        await using var intelligenceDb = new IntelligenceDbContext(intelligenceOptions);
        await using var financeDb = new FullWorthDbContext(financeOptions);
        await intelligenceDb.Database.EnsureCreatedAsync();
        await financeDb.Database.EnsureCreatedAsync();

        var suggestion = new IntelligenceSuggestion
        {
            FullWorthSpaceId = Guid.NewGuid(),
            Type = "merchant-category",
            SubjectType = "merchant",
            SubjectId = "UNKNOWN SHOP",
            SemanticKey = "merchant-category:expense",
            ProposedPayloadJson = "{\"categoryKey\":\"shopping\",\"direction\":\"expense\"}",
            EvidenceJson = "{}",
            Provider = "fake",
            Model = "fake-model",
            Confidence = 0.65m
        };
        intelligenceDb.IntelligenceSuggestions.Add(suggestion);
        await intelligenceDb.SaveChangesAsync();
        var service = new IntelligenceSuggestionReviewService(intelligenceDb, financeDb);

        var result = await service.RejectAsync(suggestion.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(await intelligenceDb.LearnedMerchantMappings.ToListAsync());
        Assert.Equal(IntelligenceSuggestionStatuses.Rejected,
            (await intelligenceDb.IntelligenceSuggestions.SingleAsync()).Status);
        Assert.Equal("ai_suggestion_rejected",
            (await intelligenceDb.IntelligenceFeedbackEvents.SingleAsync()).EventType);
    }
}
