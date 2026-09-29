using Microsoft.Extensions.Logging.Abstractions;
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
    /// <summary>Die Rueckmeldung ist Nebensache dieser Tests; sie prueft IntelligenceFeedbackRecorderTests.</summary>
    private static IntelligenceFeedbackRecorder Recorder(IntelligenceDbContext db) =>
        new(db, NullLogger<IntelligenceFeedbackRecorder>.Instance);

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
        var service = new IntelligenceSuggestionReviewService(intelligenceDb, financeDb, Recorder(intelligenceDb), []);

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

        // Hier stand "Assert.False". Das war die Luecke, nicht die Regel: eine bestaetigte Zuordnung
        // ist teilbares Wissen - "REWE ist Lebensmittel" gilt fuer jeden und enthaelt niemanden.
        Assert.True(feedback.Generalizable);

        // Verallgemeinerbar heisst nicht verschickt: es gibt keinen Empfaenger mehr. Das Flag sagt,
        // dass aus dieser Zeile instanzweites Wissen werden darf - nicht, dass sie die Maschine verlaesst.

        // Und jetzt tatsaechlich instanzweites Wissen: InstanceMerchantMappings hatte seit der
        // Cloud-Abschaffung einen Leser (TransactionRuleEngine), aber keinen Schreiber mehr. Ein
        // Mensch, der zustimmt, ist mehr wert als jede unbestaetigte KI-Antwort - 0,95, nicht 0,55.
        var instanceMapping = await intelligenceDb.InstanceMerchantMappings.SingleAsync();
        Assert.Equal("REWE", instanceMapping.AliasKey);
        Assert.Equal("expense", instanceMapping.Direction);
        Assert.Equal("food.groceries", instanceMapping.CategoryKey);
        Assert.Equal("GLOBAL", instanceMapping.Country);
        Assert.Equal(0.95m, instanceMapping.Confidence);
    }

    /// <summary>
    /// Eine zweite Bestaetigung fuer denselben Haendler und dieselbe Richtung aktualisiert die
    /// bestehende Zeile, statt eine zweite mit demselben Schluessel anzulegen - der eindeutige Index
    /// (AliasKey, Direction, Country) wuerde eine zweite ohnehin ablehnen.
    /// </summary>
    [Fact]
    public async Task A_second_confirmation_for_the_same_merchant_updates_the_existing_instance_mapping()
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
        var groceries = new FinanceCategory { FullWorthSpaceId = space.Id, Key = "food.groceries", Name = "Lebensmittel", IsSystem = true };
        var shopping = new FinanceCategory { FullWorthSpaceId = space.Id, Key = "shopping.general", Name = "Einkaufen", IsSystem = true };
        financeDb.FullWorthSpaces.Add(space);
        financeDb.Categories.AddRange(groceries, shopping);
        await financeDb.SaveChangesAsync();
        intelligenceDb.InstanceMerchantMappings.Add(new InstanceMerchantMapping
        {
            AliasKey = "REWE", Direction = "expense", CategoryKey = shopping.Key, Country = "GLOBAL", Confidence = 0.95m
        });
        await intelligenceDb.SaveChangesAsync();

        var suggestion = new IntelligenceSuggestion
        {
            FullWorthSpaceId = space.Id,
            Type = "merchant-category",
            SubjectType = "merchant",
            SubjectId = "REWE",
            SemanticKey = "merchant-category:expense",
            ProposedPayloadJson = JsonSerializer.Serialize(new { categoryKey = groceries.Key, direction = "expense" }),
            EvidenceJson = "{}",
            Provider = "fake",
            Model = "fake-model",
            Confidence = 0.9m
        };
        intelligenceDb.IntelligenceSuggestions.Add(suggestion);
        await intelligenceDb.SaveChangesAsync();
        var service = new IntelligenceSuggestionReviewService(intelligenceDb, financeDb, Recorder(intelligenceDb), []);

        var result = await service.AcceptAsync(suggestion.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        var instanceMapping = await intelligenceDb.InstanceMerchantMappings.SingleAsync();
        Assert.Equal("food.groceries", instanceMapping.CategoryKey);
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
        var service = new IntelligenceSuggestionReviewService(intelligenceDb, financeDb, Recorder(intelligenceDb), []);

        var result = await service.RejectAsync(suggestion.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(await intelligenceDb.LearnedMerchantMappings.ToListAsync());
        Assert.Equal(IntelligenceSuggestionStatuses.Rejected,
            (await intelligenceDb.IntelligenceSuggestions.SingleAsync()).Status);
        Assert.Equal("ai_suggestion_rejected",
            (await intelligenceDb.IntelligenceFeedbackEvents.SingleAsync()).EventType);
    }
}
