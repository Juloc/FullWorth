using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Transactions;
using Xunit;

namespace FullWorth.Backend.Tests.Categories;

public sealed class GermanyCategorizationCatalogTests
{
    private static FinanceTransaction Tx(
        decimal amount = -10m,
        string? counterparty = null,
        string? description = null,
        string? mcc = null) => new()
    {
        AccountId = Guid.NewGuid(),
        ExternalKey = Guid.NewGuid().ToString("N"),
        Amount = amount,
        Currency = "EUR",
        Counterparty = counterparty,
        Description = description,
        MerchantCategoryCode = mcc
    };

    [Theory]
    [InlineData("REWE MARKT 1234 BERLIN", "food.groceries")]
    [InlineData("LIDL DIENSTLEISTUNG GMBH", "food.groceries")]
    [InlineData("DM DROGERIE MARKT", "shopping.drugstore")]
    [InlineData("SHELL STATION 1234", "vehicle.fuel")]
    [InlineData("DEUTSCHE BAHN AG", "transport.public")]
    [InlineData("NETFLIX.COM", "subscriptions.streaming")]
    [InlineData("FRESSNAPF 4711", "pets.food")]
    public void KnownGermanMerchantsMatchExpectedSemanticCategory(string counterparty, string expectedKey)
    {
        var result = GermanyCategorizationCatalog.Classify(Tx(counterparty: counterparty));

        Assert.NotNull(result);
        Assert.Equal(expectedKey, result.Value.CategoryKey);
    }

    [Fact]
    public void SpecificChargingBrandWinsBeforeFuelBrand()
    {
        var result = GermanyCategorizationCatalog.Classify(Tx(counterparty: "ARAL PULSE BERLIN"));

        Assert.NotNull(result);
        Assert.Equal("vehicle.charging", result.Value.CategoryKey);
    }

    [Fact]
    public void PositiveMerchantRefundIsNotClassifiedAsExpenseMerchant()
    {
        var result = GermanyCategorizationCatalog.Classify(Tx(
            amount: 42m,
            counterparty: "AMAZON EU SARL",
            description: "RUECKERSTATTUNG BESTELLUNG"));

        Assert.NotNull(result);
        Assert.Equal("income.refunds", result.Value.CategoryKey);
    }

    [Fact]
    public void SalaryTextRequiresIncomeDirection()
    {
        var income = GermanyCategorizationCatalog.Classify(Tx(
            amount: 2500m,
            counterparty: "MUSTER GMBH",
            description: "GEHALT AUGUST 2026"));
        var expense = GermanyCategorizationCatalog.Classify(Tx(
            amount: -2500m,
            counterparty: "MUSTER GMBH",
            description: "GEHALT AUGUST 2026"));

        Assert.NotNull(income);
        Assert.Equal("income.salary", income.Value.CategoryKey);
        Assert.Null(expense);
    }

    [Fact]
    public void MccActsAsFallbackForUnknownMerchant()
    {
        var result = GermanyCategorizationCatalog.Classify(Tx(
            counterparty: "UNBEKANNTER SUPERMARKT 4711",
            mcc: "5411"));

        Assert.NotNull(result);
        Assert.Equal("food.groceries", result.Value.CategoryKey);
    }

    [Fact]
    public void PaymentIntermediaryAloneIsNotGuessed()
    {
        var paypal = GermanyCategorizationCatalog.Classify(Tx(counterparty: "PAYPAL EUROPE"));
        var klarna = GermanyCategorizationCatalog.Classify(Tx(counterparty: "KLARNA BANK AB"));

        Assert.Null(paypal);
        Assert.Null(klarna);
    }

    [Fact]
    public void PersonalRuleOverridesBuiltInCatalog()
    {
        var customCategoryId = Guid.NewGuid();
        var groceryCategoryId = Guid.NewGuid();
        var rule = new CategorizationRule
        {
            Name = "REWE Haushalt",
            IsEnabled = true,
            Priority = 1,
            Target = "transaction",
            MatchField = "counterparty",
            MatchMode = "contains",
            Pattern = "REWE",
            Direction = "expense",
            CategoryId = customCategoryId,
            StopProcessing = true
        };
        var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["food.groceries"] = groceryCategoryId
        };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            Tx(counterparty: "REWE MARKT 1234"),
            new[] { rule },
            categories);

        Assert.Equal("rule", result.Source);
        Assert.Equal(customCategoryId, result.CategoryId);
    }

    [Fact]
    public void ImportedClassificationIsPreservedWhenNoPersonalRuleMatches()
    {
        var importedCategoryId = Guid.NewGuid();
        var groceryCategoryId = Guid.NewGuid();
        var tx = Tx(counterparty: "REWE MARKT 1234");
        tx.CategoryId = importedCategoryId;
        tx.CategorizationSource = "finanzguru";
        var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["food.groceries"] = groceryCategoryId
        };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            tx,
            Array.Empty<CategorizationRule>(),
            categories);

        Assert.Equal("finanzguru", result.Source);
        Assert.Equal(importedCategoryId, result.CategoryId);
    }

    [Fact]
    public void PersonalRuleMayOverrideImportedClassification()
    {
        var importedCategoryId = Guid.NewGuid();
        var customCategoryId = Guid.NewGuid();
        var tx = Tx(counterparty: "REWE MARKT 1234");
        tx.CategoryId = importedCategoryId;
        tx.CategorizationSource = "finanzguru";
        var rule = new CategorizationRule
        {
            Name = "REWE Custom",
            IsEnabled = true,
            Priority = 1,
            Target = "transaction",
            MatchField = "counterparty",
            MatchMode = "contains",
            Pattern = "REWE",
            Direction = "expense",
            CategoryId = customCategoryId,
            StopProcessing = true
        };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            tx,
            new[] { rule },
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("rule", result.Source);
        Assert.Equal(customCategoryId, result.CategoryId);
    }

    [Fact]
    public void CatalogFallsBackToActiveParentWhenDetailedCategoryIsUnavailable()
    {
        var parentId = Guid.NewGuid();
        var categories = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["food"] = parentId
        };

        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            Tx(counterparty: "REWE MARKT 1234"),
            Array.Empty<CategorizationRule>(),
            categories);

        Assert.Equal("catalog", result.Source);
        Assert.Equal(parentId, result.CategoryId);
    }

    [Fact]
    public void CatalogDoesNotInventCategoryWhenSemanticCategoryIsUnavailable()
    {
        var result = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            Tx(counterparty: "REWE MARKT 1234"),
            Array.Empty<CategorizationRule>(),
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("none", result.Source);
        Assert.Null(result.CategoryId);
    }
}
