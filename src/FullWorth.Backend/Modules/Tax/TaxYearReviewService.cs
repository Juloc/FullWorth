using FullWorth.Backend.Data;

namespace FullWorth.Backend.Modules.Tax;

public sealed record TaxYearReviewCheck(
    string Code,
    string Severity,
    int Count,
    string Message);

public sealed record TaxYearReview(
    int TaxYear,
    bool Ready,
    int CandidateCount,
    int OpenReviewCount,
    int MissingDocumentCount,
    int IncompleteCount,
    int DuplicateSourceCount,
    IReadOnlyList<TaxYearReviewCheck> Checks);

public sealed class TaxYearReviewService(FullWorthDbContext db, TaxStore store)
{
    public async Task<TaxYearReview?> BuildAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        int taxYear,
        CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(userId, fullWorthSpaceId, ct);
        if (settings is null) return null;

        var candidates = await new TaxCandidateViewStore(db, store)
            .ListAsync(userId, fullWorthSpaceId, taxYear, null, ct);
        if (candidates is null) return null;

        var active = candidates
            .Where(x => x.Status is not TaxCandidateStatuses.Rejected and not TaxCandidateStatuses.Ignored)
            .ToList();

        var openReview = active.Count(x => x.Status is
            TaxCandidateStatuses.Detected or
            TaxCandidateStatuses.NeedsReview or
            TaxCandidateStatuses.NeedsDocument or
            TaxCandidateStatuses.Incomplete);

        // A missing receipt/invoice can be asserted safely for purchases because their document
        // relationship is canonical. For plain bank transactions we deliberately do not infer that
        // evidence is missing: it may exist outside FullWorth. The check itself is disabled when the
        // user has opted out of document analysis.
        var missingDocuments = settings.AnalyzeDocuments
            ? active.Count(x =>
                (x.SourceType is TaxSourceTypes.Purchase or TaxSourceTypes.PurchaseItem) && !x.HasDocument)
            : 0;

        var incomplete = active.Count(x =>
            x.Status == TaxCandidateStatuses.Incomplete ||
            !x.TaxCategoryId.HasValue ||
            x.EligiblePercentage is < 0m or > 100m);

        var duplicateSources = active
            .Where(x => x.SourceId.HasValue && !string.IsNullOrWhiteSpace(x.SourceType))
            .GroupBy(x => new { x.SourceType, x.SourceId })
            .Count(group => group.Count() > 1);

        var recurring = AnalyzeRecurringHints(active, taxYear);

        var checks = new List<TaxYearReviewCheck>();
        Add(checks, "review_open", openReview, "warning",
            openReview == 1 ? "1 Steuerhinweis ist noch nicht abschließend geprüft." : $"{openReview} Steuerhinweise sind noch nicht abschließend geprüft.");
        Add(checks, "documents_missing", missingDocuments, "warning",
            missingDocuments == 1 ? "Für 1 steuerlich relevanten Kauf fehlt ein verknüpfter Beleg." : $"Für {missingDocuments} steuerlich relevante Käufe fehlt ein verknüpfter Beleg.");
        Add(checks, "incomplete", incomplete, "error",
            incomplete == 1 ? "1 Steuerhinweis ist unvollständig." : $"{incomplete} Steuerhinweise sind unvollständig.");
        Add(checks, "duplicate_source", duplicateSources, "error",
            duplicateSources == 1 ? "1 Quelle wurde mehrfach als aktiver Steuerhinweis erfasst." : $"{duplicateSources} Quellen wurden mehrfach als aktive Steuerhinweise erfasst.");
        Add(checks, "recurring_gap", recurring.GapCount, "warning",
            recurring.GapCount == 1 ? "Bei 1 regelmäßig wiederkehrenden Fall gibt es eine auffällige zeitliche Lücke." : $"Bei {recurring.GapCount} regelmäßig wiederkehrenden Fällen gibt es auffällige zeitliche Lücken.");
        Add(checks, "recurring_value_change", recurring.ValueChangeCount, "warning",
            recurring.ValueChangeCount == 1 ? "Bei 1 wiederkehrenden Fall weicht ein Betrag deutlich vom üblichen Wert ab." : $"Bei {recurring.ValueChangeCount} wiederkehrenden Fällen weichen Beträge deutlich vom üblichen Wert ab.");

        if (checks.Count == 0)
            checks.Add(new TaxYearReviewCheck("ready", "success", 0,
                settings.AnalyzeDocuments
                    ? "Alle erkannten Steuerhinweise sind geprüft und die in FullWorth erwarteten Belege sind vorhanden."
                    : "Alle erkannten Steuerhinweise sind geprüft."));

        return new TaxYearReview(
            taxYear,
            checks.All(x => x.Severity == "success"),
            candidates.Count,
            openReview,
            missingDocuments,
            incomplete,
            duplicateSources,
            checks);
    }

    private static RecurringHints AnalyzeRecurringHints(IReadOnlyCollection<TaxCandidateView> candidates, int taxYear)
    {
        var gapCount = 0;
        var valueChangeCount = 0;
        var yearEnd = new DateOnly(taxYear, 12, 31);

        var groups = candidates
            .Where(x => x.SourceDate.HasValue && !string.IsNullOrWhiteSpace(x.SourceTitle) && x.GrossAmount > 0m)
            .GroupBy(x => $"{Normalize(x.SourceTitle)}|{x.TaxCategoryCode ?? string.Empty}", StringComparer.Ordinal)
            .Where(group => group.Count() >= 3);

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(x => x.SourceDate).ToList();
            var distinctMonths = ordered.Select(x => (x.SourceDate!.Value.Year, x.SourceDate.Value.Month)).Distinct().Count();
            if (distinctMonths < 3) continue;

            var gaps = ordered.Zip(ordered.Skip(1), (left, right) => right.SourceDate!.Value.DayNumber - left.SourceDate!.Value.DayNumber)
                .Where(days => days > 0)
                .ToArray();
            if (gaps.Length < 2) continue;

            var medianGap = Median(gaps.Select(x => (decimal)x).ToArray());
            // Keep this deliberately conservative. Only a stable roughly-monthly pattern becomes a
            // review hint; this is not a legal or accounting conclusion about recurrence.
            if (medianGap is < 20m or > 40m) continue;

            var daysToYearEnd = yearEnd.DayNumber - ordered[^1].SourceDate!.Value.DayNumber;
            if (gaps.Any(days => days >= 50) || daysToYearEnd >= 50)
                gapCount++;

            var medianAmount = Median(ordered.Select(x => decimal.Abs(x.GrossAmount)).ToArray());
            if (medianAmount <= 0m) continue;
            var threshold = Math.Max(5m, medianAmount * 0.35m);
            if (ordered.Any(x => decimal.Abs(decimal.Abs(x.GrossAmount) - medianAmount) > threshold))
                valueChangeCount++;
        }

        return new RecurringHints(gapCount, valueChangeCount);
    }

    private static decimal Median(decimal[] values)
    {
        if (values.Length == 0) return 0m;
        Array.Sort(values);
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2m
            : values[middle];
    }

    private static string Normalize(string value) => string.Join(' ', value
        .Trim()
        .ToLowerInvariant()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void Add(List<TaxYearReviewCheck> checks, string code, int count, string severity, string message)
    {
        if (count > 0) checks.Add(new TaxYearReviewCheck(code, severity, count, message));
    }

    private sealed record RecurringHints(int GapCount, int ValueChangeCount);
}
