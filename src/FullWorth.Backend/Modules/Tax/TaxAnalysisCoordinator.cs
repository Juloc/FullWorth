using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Tax;

public sealed record TaxAiCandidateContext(
    Guid CandidateId,
    int TaxYear,
    string CountryCode,
    string SourceTitle,
    decimal GrossAmount,
    string Currency,
    string? CurrentCategoryCode,
    decimal CurrentConfidence,
    string ReasonCode,
    string Explanation);

public sealed record TaxAiCandidateSuggestion(
    string? TaxCategoryCode,
    decimal Confidence,
    decimal? EligiblePercentage,
    string Explanation);

/// <summary>
/// Optional instance-provided AI boundary. FullWorth does not require an AI provider: deterministic
/// rules remain the primary detector and an installation without a provider behaves identically.
/// Providers must return suggestions only; this layer never confirms tax treatment automatically.
/// </summary>
public interface ITaxAiResolver
{
    Task<TaxAiCandidateSuggestion?> SuggestAsync(TaxAiCandidateContext context, CancellationToken ct);
}

public sealed class TaxAnalysisCoordinator(
    FullWorthDbContext db,
    TaxStore store,
    TaxAnalysisService deterministic,
    ITaxAiResolver? aiResolver = null)
{
    public Task<TaxAnalysisResult?> AnalyzeAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        int taxYear,
        CancellationToken ct) => AnalyzeAsync(userId, fullWorthSpaceId, taxYear, "manual", ct);

    public async Task<TaxAnalysisResult?> AnalyzeAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        int taxYear,
        string trigger,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var result = await deterministic.AnalyzeAsync(userId, fullWorthSpaceId, taxYear, ct);
        if (result is null || !result.Enabled) return result;

        if (!string.Equals(trigger, "manual", StringComparison.Ordinal))
        {
            var profileId = await db.TaxProfiles.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && x.Active)
                .Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(ct);
            if (profileId.HasValue)
            {
                var run = await db.TaxAnalysisRuns
                    .Where(x => x.TaxProfileId == profileId.Value && x.TaxYear == taxYear && x.StartedAt >= startedAt.AddSeconds(-2))
                    .OrderByDescending(x => x.StartedAt)
                    .FirstOrDefaultAsync(ct);
                if (run is not null)
                {
                    run.Trigger = trigger;
                    await db.SaveChangesAsync(ct);
                }
            }
        }

        var settings = await db.TaxSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.FullWorthSpaceId == fullWorthSpaceId, ct);

        if (settings?.AnalyzeDocuments == true)
            await ReconcileDocumentStateAsync(userId, fullWorthSpaceId, taxYear, ct);
        else
            await ClearDocumentRequirementsAsync(userId, fullWorthSpaceId, taxYear, ct);

        if (string.Equals(trigger, "manual", StringComparison.Ordinal) &&
            settings?.AiAnalysisEnabled == true &&
            string.Equals(settings.CountryCode, "DE", StringComparison.OrdinalIgnoreCase) &&
            aiResolver is not null)
            await ApplyAiSuggestionsAsync(userId, fullWorthSpaceId, taxYear, ct);

        return result;
    }

    private async Task ClearDocumentRequirementsAsync(Guid userId, Guid fullWorthSpaceId, int taxYear, CancellationToken ct)
    {
        var profileId = await db.TaxProfiles.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && x.Active)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);
        if (!profileId.HasValue) return;

        var candidates = await db.TaxCandidates
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.TaxProfileId == profileId.Value && x.TaxYear == taxYear)
            .Where(x => x.Status == TaxCandidateStatuses.NeedsDocument)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in candidates)
        {
            candidate.Status = TaxCandidateStatuses.NeedsReview;
            candidate.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileDocumentStateAsync(Guid userId, Guid fullWorthSpaceId, int taxYear, CancellationToken ct)
    {
        var profileId = await db.TaxProfiles.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId && x.Active)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);
        if (!profileId.HasValue) return;

        var candidates = await db.TaxCandidates
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.TaxProfileId == profileId.Value && x.TaxYear == taxYear)
            .Where(x => x.Status == TaxCandidateStatuses.NeedsReview || x.Status == TaxCandidateStatuses.NeedsDocument || x.Status == TaxCandidateStatuses.Detected)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        var ids = candidates.Select(x => x.Id).ToArray();
        var primary = await db.TaxCandidateSources.AsNoTracking()
            .Where(x => ids.Contains(x.TaxCandidateId) && x.IsPrimary &&
                (x.SourceType == TaxSourceTypes.Purchase || x.SourceType == TaxSourceTypes.PurchaseItem))
            .ToListAsync(ct);
        if (primary.Count == 0) return;

        var itemIds = primary.Where(x => x.SourceType == TaxSourceTypes.PurchaseItem).Select(x => x.SourceId).Distinct().ToArray();
        var itemPurchases = await db.PurchaseItems.AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .Select(x => new { x.Id, x.PurchaseId })
            .ToDictionaryAsync(x => x.Id, x => x.PurchaseId, ct);

        var candidatePurchaseIds = primary.Select(source =>
            source.SourceType == TaxSourceTypes.Purchase
                ? new { source.TaxCandidateId, PurchaseId = (Guid?)source.SourceId }
                : new { source.TaxCandidateId, PurchaseId = itemPurchases.TryGetValue(source.SourceId, out var purchaseId) ? (Guid?)purchaseId : null })
            .Where(x => x.PurchaseId.HasValue)
            .ToList();
        var purchaseIds = candidatePurchaseIds.Select(x => x.PurchaseId!.Value).Distinct().ToArray();
        var withDocument = await db.PurchaseDocuments.AsNoTracking()
            .Where(x => purchaseIds.Contains(x.PurchaseId))
            .Select(x => x.PurchaseId)
            .Distinct()
            .ToListAsync(ct);
        var documentSet = withDocument.ToHashSet();
        var purchaseByCandidate = candidatePurchaseIds.ToDictionary(x => x.TaxCandidateId, x => x.PurchaseId!.Value);

        var changed = false;
        foreach (var candidate in candidates)
        {
            if (!purchaseByCandidate.TryGetValue(candidate.Id, out var purchaseId)) continue;
            var next = documentSet.Contains(purchaseId) ? TaxCandidateStatuses.NeedsReview : TaxCandidateStatuses.NeedsDocument;
            if (candidate.Status == next) continue;
            candidate.Status = next;
            candidate.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private async Task ApplyAiSuggestionsAsync(Guid userId, Guid fullWorthSpaceId, int taxYear, CancellationToken ct)
    {
        var views = await new TaxCandidateViewStore(db, store).ListAsync(userId, fullWorthSpaceId, taxYear, null, ct);
        if (views is null) return;

        var categories = await db.TaxCategories.AsNoTracking()
            .Where(x => x.CountryCode == "DE" && x.Active && x.ValidFromTaxYear <= taxYear && (!x.ValidUntilTaxYear.HasValue || x.ValidUntilTaxYear >= taxYear))
            .ToListAsync(ct);
        var categoryByCode = categories.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

        foreach (var view in views.Where(x => x.Status is TaxCandidateStatuses.NeedsReview or TaxCandidateStatuses.NeedsDocument or TaxCandidateStatuses.Detected))
        {
            if (view.Confidence >= 0.85m) continue;

            TaxAiCandidateSuggestion? suggestion;
            try
            {
                suggestion = await aiResolver!.SuggestAsync(new TaxAiCandidateContext(
                    view.Id, taxYear, "DE", Truncate(view.SourceTitle, 240), view.GrossAmount, view.Currency,
                    view.TaxCategoryCode, view.Confidence, view.ReasonCode, Truncate(view.Explanation, 800)), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { continue; }

            if (suggestion is null || suggestion.Confidence is < 0.40m or > 1.00m) continue;
            if (suggestion.EligiblePercentage is < 0m or > 100m) continue;

            var candidate = await db.TaxCandidates.SingleOrDefaultAsync(x =>
                x.Id == view.Id && x.FullWorthSpaceId == fullWorthSpaceId &&
                x.Status != TaxCandidateStatuses.Confirmed &&
                x.Status != TaxCandidateStatuses.Rejected &&
                x.Status != TaxCandidateStatuses.Ignored, ct);
            if (candidate is null) continue;

            if (!string.IsNullOrWhiteSpace(suggestion.TaxCategoryCode))
            {
                if (!categoryByCode.TryGetValue(suggestion.TaxCategoryCode.Trim(), out var category)) continue;
                candidate.TaxCategoryId = category.Id;
            }
            if (suggestion.EligiblePercentage.HasValue)
            {
                candidate.EligiblePercentage = suggestion.EligiblePercentage.Value;
                candidate.EligibleAmount = decimal.Round(candidate.GrossAmount * candidate.EligiblePercentage / 100m, 2, MidpointRounding.AwayFromZero);
            }

            candidate.Confidence = Math.Max(candidate.Confidence, suggestion.Confidence);
            candidate.DetectionSource = candidate.DetectionSource == TaxDetectionSources.Ai ? TaxDetectionSources.Ai : TaxDetectionSources.Combined;
            candidate.Explanation = $"{candidate.Explanation} KI-Hinweis: {Truncate(suggestion.Explanation, 500)}".Trim();
            candidate.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string Truncate(string? value, int max)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= max ? normalized : normalized[..max];
    }
}
