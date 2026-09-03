using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Transactions;
using Xunit;

namespace FullWorth.Backend.Tests.Categories;

public sealed class CategoryIntelligenceExplanationTests
{
    private static FinanceTransaction Tx(
        string source = "none",
        decimal amount = -10m,
        string? counterparty = null,
        string? description = null,
        string? mcc = null,
        Guid? categoryId = null) => new()
    {
        AccountId = Guid.NewGuid(),
        ExternalKey = Guid.NewGuid().ToString("N"),
        Amount = amount,
        Currency = "EUR",
        Counterparty = counterparty,
        Description = description,
        MerchantCategoryCode = mcc,
        CategoryId = categoryId,
        CategorizationSource = source
    };

    [Fact]
    public void ManualClassificationIsFullyTrusted()
    {
        var result = CategoryIntelligenceExplanation.Explain(
            Tx(source: "manual", categoryId: Guid.NewGuid()),
            Array.Empty<CategorizationRule>());

        Assert.Equal(1.00m, result.Confidence);
        Assert.Equal("manual", result.ReasonCode);
    }

    [Fact]
    public void PersonalRuleExplainsMatchedRuleName()
    {
        var rule = new CategorizationRule
        {
            Name = "REWE immer Lebensmittel",
            MatchField = "counterparty",
            MatchMode = "contains",
            Pattern = "REWE",
            Direction = "expense",
            CategoryId = Guid.NewGuid(),
            StopProcessing = true
        };

        var result = CategoryIntelligenceExplanation.Explain(
            Tx(source: "rule", counterparty: "REWE MARKT 1234", categoryId: rule.CategoryId),
            new[] { rule });

        Assert.Equal(0.99m, result.Confidence);
        Assert.Equal("rule", result.ReasonCode);
        Assert.Equal(rule.Name, result.Detail);
    }

    [Fact]
    public void MerchantCatalogMatchHasHighConfidenceAndMerchantDetail()
    {
        var result = CategoryIntelligenceExplanation.Explain(
            Tx(source: "catalog", counterparty: "REWE MARKT 1234", categoryId: Guid.NewGuid()),
            Array.Empty<CategorizationRule>());

        Assert.Equal(0.97m, result.Confidence);
        Assert.Equal("merchant", result.ReasonCode);
        Assert.Equal("REWE", result.Detail);
    }

    [Fact]
    public void TextSignalIsExplainedSeparatelyFromMerchantMatch()
    {
        var result = CategoryIntelligenceExplanation.Explain(
            Tx(source: "catalog", amount: 2500m, counterparty: "MUSTER GMBH", description: "GEHALT AUGUST 2026", categoryId: Guid.NewGuid()),
            Array.Empty<CategorizationRule>());

        Assert.Equal(0.90m, result.Confidence);
        Assert.Equal("text", result.ReasonCode);
        Assert.Equal("GEHALT", result.Detail);
    }

    [Fact]
    public void MccFallbackIsVisibleAndLowerConfidence()
    {
        var result = CategoryIntelligenceExplanation.Explain(
            Tx(source: "catalog", counterparty: "LOKALER LADEN", mcc: "5411", categoryId: Guid.NewGuid()),
            Array.Empty<CategorizationRule>());

        Assert.Equal(0.78m, result.Confidence);
        Assert.Equal("mcc", result.ReasonCode);
        Assert.Equal("5411", result.Detail);
    }

    [Fact]
    public void ImportedClassificationIsPreservedAsTrustedExternalEvidence()
    {
        var result = CategoryIntelligenceExplanation.Explain(
            Tx(source: "finanzguru", categoryId: Guid.NewGuid()),
            Array.Empty<CategorizationRule>());

        Assert.Equal(0.95m, result.Confidence);
        Assert.Equal("imported", result.ReasonCode);
        Assert.Equal("finanzguru", result.Detail);
    }

    [Fact]
    public void UnclassifiedTransactionHasZeroConfidence()
    {
        var result = CategoryIntelligenceExplanation.Explain(
            Tx(),
            Array.Empty<CategorizationRule>());

        Assert.Equal(0m, result.Confidence);
        Assert.Equal("unclassified", result.ReasonCode);
        Assert.Null(result.Detail);
    }
}
