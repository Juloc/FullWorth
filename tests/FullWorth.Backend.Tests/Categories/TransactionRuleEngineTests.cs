using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Transactions;
using Xunit;

namespace FullWorth.Backend.Tests.Categories;

// Pure tests for the shared rule predicate. MatchesRule is the single source of truth for both
// ingestion/reapply (Evaluate) and the draft preview, so these lock its condition semantics.
public sealed class TransactionRuleEngineTests
{
    private static CategorizationRule Rule(
        string field = "counterparty", string mode = "contains", string pattern = "ACME",
        string direction = "any", decimal? min = null, decimal? max = null, string? mcc = null) => new()
    {
        Name = "r", IsEnabled = true, Priority = 100, Target = "transaction",
        MatchField = field, MatchMode = mode, Pattern = pattern, Direction = direction,
        MinAmount = min, MaxAmount = max, MerchantCategoryCode = mcc, CategoryId = Guid.NewGuid()
    };

    private static FinanceTransaction Tx(decimal amount = -10m, string? counterparty = "ACME MARKET",
        string? description = null, string? mcc = null) => new()
    {
        AccountId = Guid.NewGuid(), ExternalKey = "k", Amount = amount,
        Counterparty = counterparty, Description = description, MerchantCategoryCode = mcc
    };

    [Fact]
    public void TextModesMatchCaseInsensitively()
    {
        Assert.True(TransactionRuleEngine.MatchesRule(Tx(), Rule(mode: "contains", pattern: "acme")));
        Assert.True(TransactionRuleEngine.MatchesRule(Tx(), Rule(mode: "starts_with", pattern: "acme")));
        Assert.True(TransactionRuleEngine.MatchesRule(Tx(counterparty: "SHOP ACME"), Rule(mode: "ends_with", pattern: "acme")));
        Assert.True(TransactionRuleEngine.MatchesRule(Tx(counterparty: "ACME"), Rule(mode: "equals", pattern: "acme")));
        Assert.False(TransactionRuleEngine.MatchesRule(Tx(counterparty: "ACME MARKET"), Rule(mode: "equals", pattern: "acme")));
    }

    [Fact]
    public void DirectionFiltersBySign()
    {
        Assert.False(TransactionRuleEngine.MatchesRule(Tx(amount: -10m), Rule(direction: "income")));
        Assert.True(TransactionRuleEngine.MatchesRule(Tx(amount: 10m), Rule(direction: "income")));
        Assert.True(TransactionRuleEngine.MatchesRule(Tx(amount: -10m), Rule(direction: "expense")));
        Assert.False(TransactionRuleEngine.MatchesRule(Tx(amount: 10m), Rule(direction: "expense")));
    }

    [Fact]
    public void AmountRangeUsesAbsoluteValue()
    {
        Assert.True(TransactionRuleEngine.MatchesRule(Tx(amount: -25m), Rule(min: 10m, max: 50m)));
        Assert.False(TransactionRuleEngine.MatchesRule(Tx(amount: -5m), Rule(min: 10m, max: 50m)));
        Assert.False(TransactionRuleEngine.MatchesRule(Tx(amount: -75m), Rule(min: 10m, max: 50m)));
    }

    [Fact]
    public void MccMustMatchExactlyWhenSpecified()
    {
        Assert.True(TransactionRuleEngine.MatchesRule(Tx(mcc: "5411"), Rule(pattern: "", mcc: "5411")));
        Assert.False(TransactionRuleEngine.MatchesRule(Tx(mcc: "5412"), Rule(pattern: "", mcc: "5411")));
    }

    [Fact]
    public void EvaluateRespectsPriorityAndStopProcessing()
    {
        var first = Rule(pattern: "ACME");
        first.Priority = 1; first.StopProcessing = true;
        var second = Rule(pattern: "ACME");
        second.Priority = 2;

        var result = TransactionRuleEngine.Evaluate(Tx(), new[] { first, second });
        Assert.Equal("rule", result.Source);
        Assert.Equal(first.CategoryId, result.CategoryId);
    }
}
