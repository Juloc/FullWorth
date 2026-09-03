namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Cost estimates are configuration-driven so model pricing is never hardcoded into FullWorth.
/// When an AI budget is configured and no estimate exists, AiBudgetGuard intentionally blocks the call.
/// Example key: Intelligence:CostEstimates:openai:gpt-5.6:text-classification:EstimatedCallCostEur
/// </summary>
public sealed class AiCostEstimator(IConfiguration configuration)
{
    public decimal? GetEstimatedCallCostEur(string provider, string model, string capability)
    {
        var key = $"Intelligence:CostEstimates:{provider}:{model}:{capability}:EstimatedCallCostEur";
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0m)
            throw new InvalidOperationException($"Invalid AI cost estimate configuration for '{provider}/{model}/{capability}'.");
        return value;
    }
}
