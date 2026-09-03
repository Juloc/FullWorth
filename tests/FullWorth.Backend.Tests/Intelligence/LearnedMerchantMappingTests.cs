using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class LearnedMerchantMappingTests
{
    [Fact]
    public async Task Ordinary_category_correction_records_feedback_without_future_mapping()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var recorder = new IntelligenceFeedbackRecorder(db, NullLogger<IntelligenceFeedbackRecorder>.Instance);
        var categoryId = Guid.NewGuid();

        Assert.True(await recorder.RecordCategoryDecisionAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "REWE Markt 123!", "expense",
            null, categoryId, "category_changed", CancellationToken.None));

        Assert.Empty(await db.LearnedMerchantMappings.ToListAsync());
        var feedback = await db.IntelligenceFeedbackEvents.SingleAsync();
        Assert.Equal("category_changed", feedback.EventType);
        Assert.Equal("transaction_category", feedback.SubjectType);
        Assert.Contains(categoryId.ToString("D"), feedback.NewValueJson);
        Assert.DoesNotContain("REWE", feedback.OldValueJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REWE", feedback.NewValueJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REWE", feedback.SubjectFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Learned_mapping_overrides_import_but_explicit_rule_overrides_mapping()
    {
        var importedCategoryId = Guid.NewGuid();
        var learnedCategoryId = Guid.NewGuid();
        var ruleCategoryId = Guid.NewGuid();
        var tx = new FinanceTransaction
        {
            Amount = -42m,
            NormalizedCounterparty = "REWE MARKT",
            CategoryId = importedCategoryId,
            CategorizationSource = "import"
        };
        var mappings = new[]
        {
            new LearnedMerchantCategoryMapping("REWE MARKT", "expense", learnedCategoryId)
        };

        var learned = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            tx, Array.Empty<CategorizationRule>(), new Dictionary<string, Guid>(), mappings);

        Assert.Equal(learnedCategoryId, learned.CategoryId);
        Assert.Equal("learned", learned.Source);

        var rules = new[]
        {
            new CategorizationRule
            {
                MatchField = "normalized_counterparty",
                MatchMode = "equals",
                Pattern = "REWE MARKT",
                Direction = "expense",
                CategoryId = ruleCategoryId,
                StopProcessing = true
            }
        };
        var ruled = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            tx, rules, new Dictionary<string, Guid>(), mappings);

        Assert.Equal(ruleCategoryId, ruled.CategoryId);
        Assert.Equal("rule", ruled.Source);
    }

    [Fact]
    public void Learned_mapping_requires_exact_counterparty_and_direction()
    {
        var importedCategoryId = Guid.NewGuid();
        var learnedCategoryId = Guid.NewGuid();
        var mappings = new[]
        {
            new LearnedMerchantCategoryMapping("REWE MARKT", "expense", learnedCategoryId)
        };

        var differentMerchant = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            new FinanceTransaction
            {
                Amount = -10m,
                NormalizedCounterparty = "REWE MARKT FILIALE",
                CategoryId = importedCategoryId,
                CategorizationSource = "import"
            }, Array.Empty<CategorizationRule>(), new Dictionary<string, Guid>(), mappings);
        Assert.Equal(importedCategoryId, differentMerchant.CategoryId);
        Assert.Equal("import", differentMerchant.Source);

        var differentDirection = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            new FinanceTransaction
            {
                Amount = 10m,
                NormalizedCounterparty = "REWE MARKT",
                CategoryId = importedCategoryId,
                CategorizationSource = "import"
            }, Array.Empty<CategorizationRule>(), new Dictionary<string, Guid>(), mappings);
        Assert.Equal(importedCategoryId, differentDirection.CategoryId);
        Assert.Equal("import", differentDirection.Source);
    }
}
