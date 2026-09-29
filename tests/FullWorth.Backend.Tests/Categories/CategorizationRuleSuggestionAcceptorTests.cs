using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Categories;

/// <summary>
/// Die staerkste Form eines KI-Vorschlags: keine einzelne Haendler-Zuordnung, sondern eine echte,
/// editierbare <see cref="CategorizationRule"/> unter <c>/rules</c>.
///
/// Der Acceptor lebt in Modules/Categories, obwohl er einen Intelligence-Vorschlag annimmt - siehe
/// <see cref="IIntelligenceSuggestionAcceptor"/> fuer das Warum: der umgekehrte Import waere ein
/// Modulzyklus, den die Architektur-Waechter dauerhaft verbieten.
/// </summary>
public sealed class CategorizationRuleSuggestionAcceptorTests
{
    [Fact]
    public async Task Accepting_creates_a_real_editable_rule()
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
        var category = new FinanceCategory { FullWorthSpaceId = space.Id, Key = "food.groceries", Name = "Lebensmittel", IsSystem = true };
        financeDb.FullWorthSpaces.Add(space);
        financeDb.Categories.Add(category);
        await financeDb.SaveChangesAsync();

        var suggestion = new IntelligenceSuggestion
        {
            FullWorthSpaceId = space.Id,
            Type = "categorization-rule",
            SubjectType = "merchant-pattern",
            SubjectId = "REWE",
            SemanticKey = "categorization-rule:expense:REWE",
            ProposedPayloadJson = JsonSerializer.Serialize(new
            {
                name = "REWE-Filialen",
                matchField = "normalized_counterparty",
                matchMode = "starts_with",
                pattern = "REWE",
                direction = "expense",
                categoryKey = category.Key,
                markAsTransfer = false,
                stopProcessing = true
            }),
            EvidenceJson = "{}",
            Provider = "fake",
            Model = "fake-model",
            Confidence = 0.85m
        };
        intelligenceDb.IntelligenceSuggestions.Add(suggestion);
        await intelligenceDb.SaveChangesAsync();
        var actor = Guid.NewGuid();

        var result = await Acceptor(intelligenceDb, financeDb).AcceptAsync(suggestion, actor, CancellationToken.None);

        Assert.True(result.Success);
        var rule = await financeDb.CategorizationRules.SingleAsync();
        Assert.Equal(space.Id, rule.FullWorthSpaceId);
        Assert.Equal("REWE-Filialen", rule.Name);
        Assert.Equal("starts_with", rule.MatchMode);
        Assert.Equal("REWE", rule.Pattern);
        Assert.Equal("expense", rule.Direction);
        Assert.Equal(category.Id, rule.CategoryId);
        Assert.True(rule.IsEnabled);
        Assert.Equal(IntelligenceSuggestionStatuses.Accepted,
            (await intelligenceDb.IntelligenceSuggestions.SingleAsync()).Status);
    }

    [Fact]
    public async Task An_unknown_category_key_is_refused()
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
        financeDb.FullWorthSpaces.Add(space);
        await financeDb.SaveChangesAsync();

        var suggestion = new IntelligenceSuggestion
        {
            FullWorthSpaceId = space.Id,
            Type = "categorization-rule",
            SubjectType = "merchant-pattern",
            SubjectId = "REWE",
            SemanticKey = "categorization-rule:expense:REWE",
            ProposedPayloadJson = JsonSerializer.Serialize(new
            {
                name = "REWE-Filialen",
                matchField = "normalized_counterparty",
                matchMode = "starts_with",
                pattern = "REWE",
                direction = "expense",
                categoryKey = "does.not.exist"
            }),
            EvidenceJson = "{}",
            Provider = "fake",
            Model = "fake-model",
            Confidence = 0.85m
        };
        intelligenceDb.IntelligenceSuggestions.Add(suggestion);
        await intelligenceDb.SaveChangesAsync();

        var result = await Acceptor(intelligenceDb, financeDb).AcceptAsync(suggestion, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("category_not_found", result.ErrorCode);
        Assert.Empty(await financeDb.CategorizationRules.ToListAsync());
        // Nicht angenommen, aber auch nicht abgelehnt - der naechste Versuch mit einem gueltigen
        // Schluessel soll noch moeglich sein.
        Assert.Equal(IntelligenceSuggestionStatuses.Pending,
            (await intelligenceDb.IntelligenceSuggestions.SingleAsync()).Status);
    }

    private static CategorizationRuleSuggestionAcceptor Acceptor(
        IntelligenceDbContext intelligenceDb, FullWorthDbContext financeDb) =>
        new(intelligenceDb, financeDb, new CategoryStore(financeDb));
}
