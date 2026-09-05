namespace FullWorth.Backend.Modules.Analytics;

/// <summary>
/// One reusable, auth/space-scoped analytics query (§6). Every period-aware report (overview, category,
/// merchant) uses it so they all respect the SELECTED window and granularity instead of silently forcing
/// the current calendar month. Scope filters (account / account-group / category[+descendants] / merchant)
/// are resolved and enforced server-side inside the caller's active space and accessible accounts.
/// </summary>
public sealed record AnalyticsQuery(
    DateOnly From,
    DateOnly To,
    string Granularity,
    Guid? AccountId,
    Guid? AccountGroupId,
    Guid? CategoryId,
    bool IncludeCategoryDescendants,
    Guid? MerchantId,
    string ComparisonMode,
    string Currency)
{
    public static readonly string[] Granularities = ["week", "month", "quarter", "year"];

    public string NormalizedGranularity => NormalizeGranularity(Granularity);
    public string NormalizedComparison => NormalizeComparison(ComparisonMode);

    /// <summary>
    /// Build from raw query parameters. When from/to are omitted the window defaults to the calendar
    /// month named by year/month (today's month when those are omitted too) — keeping the legacy
    /// year/month endpoints working while newer callers pass an explicit period + granularity.
    /// </summary>
    public static AnalyticsQuery Create(
        DateOnly? from, DateOnly? to, string? granularity, int? year, int? month,
        Guid? accountId, Guid? accountGroupId, Guid? categoryId, bool? includeDescendants,
        Guid? merchantId, string? comparisonMode, string? currency, DateOnly today)
    {
        var gran = NormalizeGranularity(granularity);
        DateOnly start;
        DateOnly end;
        if (from.HasValue || to.HasValue)
        {
            start = from ?? to!.Value;
            end = to ?? from!.Value;
            if (end < start) (start, end) = (end, start);
        }
        else
        {
            var y = year ?? today.Year;
            var m = month ?? today.Month;
            start = new DateOnly(y, m, 1);
            end = start.AddMonths(1).AddDays(-1);
            if (string.IsNullOrWhiteSpace(granularity)) gran = "month";
        }

        return new AnalyticsQuery(
            start, end, gran, accountId, accountGroupId, categoryId,
            includeDescendants ?? true, merchantId, NormalizeComparison(comparisonMode), NormalizeCurrency(currency));
    }

    /// <summary>The window shifted back by <paramref name="unitsBack"/> periods of the current granularity.</summary>
    public (DateOnly Start, DateOnly End) Shifted(int unitsBack) => NormalizedGranularity switch
    {
        "week" => (From.AddDays(-7 * unitsBack), To.AddDays(-7 * unitsBack)),
        "quarter" => (From.AddMonths(-3 * unitsBack), To.AddMonths(-3 * unitsBack)),
        "year" => (From.AddYears(-unitsBack), To.AddYears(-unitsBack)),
        _ => (From.AddMonths(-unitsBack), To.AddMonths(-unitsBack)),
    };

    /// <summary>The comparison ("previous") window for the selected <see cref="ComparisonMode"/>.</summary>
    public (DateOnly Start, DateOnly End) ComparisonWindow() =>
        NormalizedComparison == "previous-year" ? (From.AddYears(-1), To.AddYears(-1)) : Shifted(1);

    private static string NormalizeGranularity(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return Granularities.Contains(v) ? v : "month";
    }

    private static string NormalizeComparison(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v is "previous-year" or "previous-period" or "none" ? v : "previous-period";
    }

    private static string NormalizeCurrency(string? currency)
    {
        var normalized = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(character => character is >= 'A' and <= 'Z') ? normalized : "EUR";
    }
}
