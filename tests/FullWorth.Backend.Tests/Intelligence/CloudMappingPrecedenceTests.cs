using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Transactions;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudMappingPrecedenceTests
{
    [Fact]
    public void Explicit_rule_beats_local_learning_and_cloud()
    {
        var ruleCategory = Guid.NewGuid();
        var learnedCategory = Guid.NewGuid();
        var cloudCategory = Guid.NewGuid();
        var tx = Expense("AMZN MKTP DE");
        var rules = new[]
        {
            new CategorizationRule
            {
                CategoryId = ruleCategory,
                MatchField = "normalized_counterparty",
                MatchMode = "equals",
                Pattern = "AMZN MKTP DE",
                Direction = "expense"
            }
        };
        var learned = new[] { new LearnedMerchantCategoryMapping("AMZN MKTP DE", "expense", learnedCategory) };
        var cloud = new[] { new OfficialMerchantCategoryMapping("AMZN MKTP DE", "expense", "shopping.online", 0.99m) };
        var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["shopping.online"] = cloudCategory };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(tx, rules, categories, learned, cloud);

        Assert.Equal(ruleCategory, result.CategoryId);
        Assert.Equal("rule", result.Source);
    }

    [Fact]
    public void Confirmed_local_learning_beats_cloud()
    {
        var learnedCategory = Guid.NewGuid();
        var cloudCategory = Guid.NewGuid();
        var tx = Expense("AMZN MKTP DE");
        var learned = new[] { new LearnedMerchantCategoryMapping("AMZN MKTP DE", "expense", learnedCategory) };
        var cloud = new[] { new OfficialMerchantCategoryMapping("AMZN MKTP DE", "expense", "shopping.online", 0.99m) };
        var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["shopping.online"] = cloudCategory };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(tx, [], categories, learned, cloud);

        Assert.Equal(learnedCategory, result.CategoryId);
        Assert.Equal("learned", result.Source);
    }

    [Fact]
    public void Imported_classification_beats_cloud()
    {
        var importedCategory = Guid.NewGuid();
        var cloudCategory = Guid.NewGuid();
        var tx = Expense("AMZN MKTP DE");
        tx.CategoryId = importedCategory;
        tx.CategorizationSource = "provider";
        var cloud = new[] { new OfficialMerchantCategoryMapping("AMZN MKTP DE", "expense", "shopping.online", 0.99m) };
        var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["shopping.online"] = cloudCategory };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(tx, [], categories, [], cloud);

        Assert.Equal(importedCategory, result.CategoryId);
        Assert.Equal("provider", result.Source);
    }

    [Fact]
    public void Verified_high_confidence_cloud_mapping_beats_builtin_catalog()
    {
        var cloudCategory = Guid.NewGuid();
        var tx = Expense("AMZN MKTP DE");
        var cloud = new[] { new OfficialMerchantCategoryMapping("AMZN MKTP DE", "expense", "shopping.online", 0.95m) };
        var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["shopping.online"] = cloudCategory };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(tx, [], categories, [], cloud);

        Assert.Equal(cloudCategory, result.CategoryId);
        Assert.Equal("cloud", result.Source);
    }

    [Fact]
    public void Low_confidence_cloud_mapping_is_ignored()
    {
        var cloudCategory = Guid.NewGuid();
        var tx = Expense("UNLISTED CLOUD MERCHANT");
        var cloud = new[] { new OfficialMerchantCategoryMapping("UNLISTED CLOUD MERCHANT", "expense", "shopping.online", 0.50m) };
        var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["shopping.online"] = cloudCategory };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(tx, [], categories, [], cloud);

        Assert.NotEqual("cloud", result.Source);
    }

    private static FinanceTransaction Expense(string normalizedCounterparty) => new()
    {
        Amount = -20m,
        Counterparty = normalizedCounterparty,
        NormalizedCounterparty = normalizedCounterparty,
        CategorizationSource = "none"
    };
}
