using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Transactions;

namespace FullWorth.Backend.Modules.Categories;

/// <summary>
/// Deterministic evaluation of transaction categorization rules. Shared by ingestion and the
/// retroactive reapply so both behave identically. Pure: computes the classification without
/// mutating the transaction.
/// </summary>
public static class TransactionRuleEngine
{
    public const decimal MinimumCloudMappingConfidence = 0.80m;

    public readonly record struct Classification(Guid? CategoryId, bool IsTransfer, string Source)
    {
        public bool MarkAsTransfer => IsTransfer;
    }

    public static Classification Evaluate(FinanceTransaction tx, IReadOnlyList<CategorizationRule> rules)
    {
        var result = new Classification(null, false, "none");
        foreach (var rule in rules)
        {
            if (!MatchesRule(tx, rule)) continue;
            result = new Classification(rule.CategoryId, rule.MarkAsTransfer, "rule");
            if (rule.StopProcessing) break;
        }
        return result;
    }

    public static Classification EvaluateWithGermanyCatalog(
        FinanceTransaction tx,
        IReadOnlyList<CategorizationRule> rules,
        IReadOnlyDictionary<string, Guid> activeCategoryIdsByKey) =>
        EvaluateWithGermanyCatalog(
            tx,
            rules,
            activeCategoryIdsByKey,
            Array.Empty<LearnedMerchantCategoryMapping>(),
            Array.Empty<OfficialMerchantCategoryMapping>());

    public static Classification EvaluateWithGermanyCatalog(
        FinanceTransaction tx,
        IReadOnlyList<CategorizationRule> rules,
        IReadOnlyDictionary<string, Guid> activeCategoryIdsByKey,
        IReadOnlyList<LearnedMerchantCategoryMapping> learnedMappings) =>
        EvaluateWithGermanyCatalog(
            tx,
            rules,
            activeCategoryIdsByKey,
            learnedMappings,
            Array.Empty<OfficialMerchantCategoryMapping>());

    /// <summary>
    /// Precedence is explicit personal rule -> exact user-confirmed local merchant mapping ->
    /// meaningful importer/user classification -> verified FullWorth Cloud mapping -> built-in catalog.
    /// Cloud mappings are exact normalized aliases only, confidence-gated, and never override local facts.
    /// </summary>
    public static Classification EvaluateWithGermanyCatalog(
        FinanceTransaction tx,
        IReadOnlyList<CategorizationRule> rules,
        IReadOnlyDictionary<string, Guid> activeCategoryIdsByKey,
        IReadOnlyList<LearnedMerchantCategoryMapping> learnedMappings,
        IReadOnlyList<OfficialMerchantCategoryMapping> cloudMappings)
    {
        var ruleResult = Evaluate(tx, rules);
        if (ruleResult.Source == "rule") return ruleResult;

        var learned = EvaluateLearnedMerchantMapping(tx, learnedMappings);
        if (learned.HasValue)
            return new Classification(learned.Value, false, "learned");

        // Preserve explicit/imported classifications. The named values below are FullWorth-owned
        // automatic states and may therefore be recomputed when better deterministic knowledge exists.
        if (tx.CategoryId.HasValue &&
            tx.CategorizationSource is not ("none" or "rule" or "learned" or "cloud" or "catalog"))
            return new Classification(tx.CategoryId, tx.IsTransfer, tx.CategorizationSource);

        var cloudCategoryKey = EvaluateCloudMerchantMapping(tx, cloudMappings);
        if (cloudCategoryKey is not null)
        {
            var resolved = ResolveCategoryId(activeCategoryIdsByKey, cloudCategoryKey);
            if (resolved.HasValue)
                return new Classification(resolved.Value, false, "cloud");
        }

        var catalogMatch = GermanyCategorizationCatalog.Classify(tx);
        if (catalogMatch is null) return ruleResult;
        var catalogCategory = ResolveCategoryId(activeCategoryIdsByKey, catalogMatch.Value.CategoryKey);
        return catalogCategory.HasValue
            ? new Classification(catalogCategory.Value, false, "catalog")
            : ruleResult;
    }

    public static Guid? EvaluateLearnedMerchantMapping(
        FinanceTransaction tx,
        IReadOnlyList<LearnedMerchantCategoryMapping> learnedMappings)
    {
        if (string.IsNullOrWhiteSpace(tx.NormalizedCounterparty) || tx.Amount == 0m || learnedMappings.Count == 0)
            return null;

        var direction = tx.Amount > 0m ? "income" : "expense";
        foreach (var mapping in learnedMappings)
        {
            if (string.Equals(mapping.Direction, direction, StringComparison.Ordinal) &&
                string.Equals(mapping.NormalizedCounterparty, tx.NormalizedCounterparty, StringComparison.Ordinal))
                return mapping.CategoryId;
        }
        return null;
    }

    public static string? EvaluateCloudMerchantMapping(
        FinanceTransaction tx,
        IReadOnlyList<OfficialMerchantCategoryMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(tx.NormalizedCounterparty) || tx.Amount == 0m || mappings.Count == 0)
            return null;

        var direction = tx.Amount > 0m ? "income" : "expense";
        return mappings
            .Where(x => x.Confidence >= MinimumCloudMappingConfidence)
            .Where(x => string.Equals(x.AliasKey, tx.NormalizedCounterparty, StringComparison.Ordinal))
            .Where(x => x.Direction == "any" || string.Equals(x.Direction, direction, StringComparison.Ordinal))
            .OrderByDescending(x => string.Equals(x.Direction, direction, StringComparison.Ordinal))
            .ThenByDescending(x => x.Confidence)
            .Select(x => x.CategoryKey)
            .FirstOrDefault();
    }

    private static Guid? ResolveCategoryId(
        IReadOnlyDictionary<string, Guid> activeCategoryIdsByKey,
        string categoryKey)
    {
        var key = categoryKey;
        while (!string.IsNullOrWhiteSpace(key))
        {
            if (activeCategoryIdsByKey.TryGetValue(key, out var categoryId)) return categoryId;
            var dot = key.LastIndexOf('.');
            if (dot < 0) break;
            key = key[..dot];
        }
        return null;
    }

    public static bool MatchesRule(FinanceTransaction tx, CategorizationRule rule)
    {
        if (rule.Direction == "income" && tx.Amount <= 0) return false;
        if (rule.Direction == "expense" && tx.Amount >= 0) return false;

        var abs = Math.Abs(tx.Amount);
        if (rule.MinAmount.HasValue && abs < rule.MinAmount.Value) return false;
        if (rule.MaxAmount.HasValue && abs > rule.MaxAmount.Value) return false;
        if (!string.IsNullOrWhiteSpace(rule.MerchantCategoryCode) &&
            !string.Equals(rule.MerchantCategoryCode, tx.MerchantCategoryCode, StringComparison.OrdinalIgnoreCase))
            return false;

        var text = rule.MatchField switch
        {
            "counterparty" => tx.Counterparty,
            "description" => tx.Description,
            "normalized_counterparty" => tx.NormalizedCounterparty,
            "mcc" => tx.MerchantCategoryCode,
            _ => string.Join(' ', new[] { tx.Counterparty, tx.NormalizedCounterparty, tx.Description, tx.MerchantCategoryCode }
                .Where(x => !string.IsNullOrWhiteSpace(x)))
        };
        return Matches(text, rule.Pattern, rule.MatchMode);
    }

    public static bool Matches(string? text, string pattern, string mode)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return mode switch
        {
            "equals" => string.Equals(text.Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            "starts_with" => text.Trim().StartsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            "ends_with" => text.Trim().EndsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => text.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase)
        };
    }
}
