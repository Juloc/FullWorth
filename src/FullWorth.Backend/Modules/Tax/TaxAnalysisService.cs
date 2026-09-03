using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Purchases;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Tax;

public sealed class TaxAnalysisService(FullWorthDbContext db, TaxStore store)
{
    private static readonly string[] SoftwareWords = ["adobe", "jetbrains", "microsoft 365", "office 365", "github", "software", "saas"];
    private static readonly string[] EducationWords = ["udemy", "coursera", "linkedin learning", "fortbildung", "weiterbildung", "seminar", "fachkurs"];
    private static readonly string[] DonationWords = ["spende", "donation", "hilfswerk", "stiftung"];
    private static readonly string[] InsuranceWords = ["versicherung", "insurance"];
    private static readonly string[] HandymanWords = ["handwerker", "elektriker", "installateur", "malerbetrieb", "sanitär", "sanitaer"];
    private static readonly string[] HouseholdServiceWords = ["haushaltsnahe", "reinigung", "gebäudereinigung", "gebaeudereinigung", "gartenpflege"];
    private static readonly string[] WorkEquipmentWords = ["arbeitsmittel", "büro", "buero", "office", "drucker", "monitor", "tastatur", "fachliteratur", "dock", "docking station"];

    public async Task<TaxAnalysisResult?> AnalyzeAsync(Guid userId, Guid fullWorthSpaceId, int taxYear, CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(userId, fullWorthSpaceId, ct);
        if (settings is null) return null;
        if (!settings.Enabled)
            return new TaxAnalysisResult(false, taxYear, 0, 0, 0, RuleVersion(settings.CountryCode, taxYear));

        var profile = await store.EnsurePersonalProfileAsync(userId, fullWorthSpaceId, ct);
        if (profile is null) return null;
        if (!profile.AssistantEnabled)
            return new TaxAnalysisResult(false, taxYear, 0, 0, 0, RuleVersion(settings.CountryCode, taxYear));

        await store.EnsureGermanCatalogAsync(ct);

        var ruleVersion = RuleVersion(settings.CountryCode, taxYear);
        var run = new TaxAnalysisRun
        {
            FullWorthSpaceId = fullWorthSpaceId,
            TaxProfileId = profile.Id,
            TaxYear = taxYear,
            Trigger = "manual",
            RuleVersion = ruleVersion
        };
        db.TaxAnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            if (!string.Equals(settings.CountryCode, "DE", StringComparison.OrdinalIgnoreCase))
            {
                await CompleteRunAsync(run, 0, 0, 0, ct);
                return new TaxAnalysisResult(true, taxYear, 0, 0, 0, ruleVersion);
            }

            var from = new DateOnly(taxYear, 1, 1);
            var to = new DateOnly(taxYear, 12, 31);
            var taxCategories = await db.TaxCategories.AsNoTracking()
                .Where(x => x.CountryCode == "DE" && x.Active && x.ValidFromTaxYear <= taxYear && (!x.ValidUntilTaxYear.HasValue || x.ValidUntilTaxYear >= taxYear))
                .ToDictionaryAsync(x => x.Code, x => x.Id, ct);

            var mappings = await db.TaxUserMappings.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.TaxProfileId == profile.Id && x.Active)
                .OrderByDescending(x => x.UpdatedAt)
                .ToListAsync(ct);
            var mappingLookup = mappings
                .GroupBy(x => (x.MatchType, x.MatchValue))
                .ToDictionary(x => x.Key, x => x.First());
            var mappingCategoryIds = mappings.Where(x => x.TaxCategoryId.HasValue)
                .Select(x => x.TaxCategoryId!.Value)
                .Distinct()
                .ToArray();
            var mappingCategoryCodes = await db.TaxCategories.AsNoTracking()
                .Where(x => mappingCategoryIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Code, ct);

            var created = 0;
            var changed = 0;
            var sourcesAnalyzed = 0;
            var suppressedTransactionIds = new HashSet<Guid>();

            if (settings.AnalyzePurchases)
            {
                var purchases = await VisiblePurchases(userId, fullWorthSpaceId)
                    .Include(x => x.Items)
                    .Include(x => x.PaymentLinks)
                    .Include(x => x.Documents)
                    .Where(x => x.PurchaseDate >= from && x.PurchaseDate <= to)
                    .OrderBy(x => x.PurchaseDate)
                    .ThenBy(x => x.Id)
                    .ToListAsync(ct);

                // Document analysis is an independent opt-in. Detached purchase projections can safely
                // drop their document navigation so receipts neither raise confidence nor enter evidence
                // fingerprints/sources when the user has disabled document analysis.
                if (!settings.AnalyzeDocuments)
                    foreach (var purchase in purchases) purchase.Documents.Clear();

                var purchaseCategoryIds = purchases.SelectMany(x => x.Items)
                    .Where(x => x.CategoryId.HasValue)
                    .Select(x => x.CategoryId!.Value)
                    .Distinct()
                    .ToArray();
                var purchaseCategoryNames = await db.Categories.AsNoTracking()
                    .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && purchaseCategoryIds.Contains(x.Id))
                    .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

                foreach (var purchase in purchases)
                {
                    var purchaseHandledTaxSource = false;
                    if (purchase.Items.Count > 0)
                    {
                        foreach (var item in purchase.Items.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
                        {
                            if (ShouldSkipItem(item)) continue;
                            sourcesAnalyzed++;

                            var userMapping = ResolveUserMapping(
                                mappingLookup,
                                mappingCategoryCodes,
                                TaxUserMappingTypes.PurchaseItemSignature,
                                TaxLearningKey.PurchaseItem(purchase.Merchant, item.RawName, item.Name, item.Brand));
                            if (userMapping.Ignore)
                            {
                                purchaseHandledTaxSource = true;
                                continue;
                            }

                            var categoryName = item.CategoryId.HasValue && purchaseCategoryNames.TryGetValue(item.CategoryId.Value, out var found)
                                ? found
                                : null;
                            var match = userMapping.Match ?? Evaluate(
                                purchase.Merchant,
                                purchase.MerchantRaw,
                                item.RawName,
                                item.Name,
                                item.Brand,
                                item.Notes,
                                purchase.Notes,
                                categoryName);
                            match = AddDocumentEvidence(match, purchase.Documents.Count > 0);
                            if (match is null || match.Confidence < 0.40m) continue;

                            var extras = PurchaseEvidenceSources(purchase).ToArray();
                            var evidenceVersion = PurchaseEvidenceVersion(purchase);
                            var outcome = await UpsertCandidateAsync(
                                fullWorthSpaceId,
                                profile.Id,
                                taxYear,
                                ruleVersion,
                                TaxSourceTypes.PurchaseItem,
                                item.Id,
                                decimal.Abs(item.TotalPrice),
                                item.Currency,
                                match,
                                Fingerprint(item.Id, item.UpdatedAt, ruleVersion, evidenceVersion),
                                extras,
                                taxCategories,
                                ct);
                            created += outcome.Created ? 1 : 0;
                            changed += outcome.Changed ? 1 : 0;
                            purchaseHandledTaxSource |= outcome.Surfaced;
                        }
                    }
                    else
                    {
                        sourcesAnalyzed++;
                        var userMapping = ResolveUserMapping(
                            mappingLookup,
                            mappingCategoryCodes,
                            TaxUserMappingTypes.PurchaseSignature,
                            TaxLearningKey.Purchase(purchase.Merchant, purchase.MerchantRaw, purchase.Notes));
                        if (userMapping.Ignore)
                        {
                            purchaseHandledTaxSource = true;
                        }
                        else
                        {
                            var match = userMapping.Match ?? Evaluate(purchase.Merchant, purchase.MerchantRaw, purchase.Notes);
                            match = AddDocumentEvidence(match, purchase.Documents.Count > 0);
                            if (match is not null && match.Confidence >= 0.40m)
                            {
                                var extras = purchase.Documents
                                    .Select(x => new SourceRef(TaxSourceTypes.PurchaseDocument, x.Id))
                                    .ToArray();
                                var outcome = await UpsertCandidateAsync(
                                    fullWorthSpaceId,
                                    profile.Id,
                                    taxYear,
                                    ruleVersion,
                                    TaxSourceTypes.Purchase,
                                    purchase.Id,
                                    decimal.Abs(purchase.TotalAmount),
                                    purchase.Currency,
                                    match,
                                    Fingerprint(purchase.Id, purchase.UpdatedAt, ruleVersion, PurchaseEvidenceVersion(purchase)),
                                    extras,
                                    taxCategories,
                                    ct);
                                created += outcome.Created ? 1 : 0;
                                changed += outcome.Changed ? 1 : 0;
                                purchaseHandledTaxSource |= outcome.Surfaced;
                            }
                        }
                    }

                    if (purchaseHandledTaxSource)
                    {
                        if (purchase.TransactionId.HasValue) suppressedTransactionIds.Add(purchase.TransactionId.Value);
                        foreach (var payment in purchase.PaymentLinks) suppressedTransactionIds.Add(payment.TransactionId);
                    }
                }
            }

            if (settings.AnalyzeTransactions)
            {
                var suppressed = suppressedTransactionIds.ToArray();
                var transactions = await (
                    from transaction in db.Transactions.AsNoTracking()
                    join account in db.Accounts.AsNoTracking() on transaction.AccountId equals account.Id
                    where account.FullWorthSpaceId == fullWorthSpaceId
                          && db.AccountOwners.Any(owner => owner.AccountId == account.Id && owner.UserId == userId)
                          && transaction.BookingDate >= @from
                          && transaction.BookingDate <= @to
                          && transaction.Amount < 0
                          && !transaction.IsIgnored
                          && !transaction.IsTransfer
                          && !suppressed.Contains(transaction.Id)
                    select transaction).ToListAsync(ct);

                sourcesAnalyzed += transactions.Count;
                var categoryIds = transactions.Where(x => x.CategoryId.HasValue).Select(x => x.CategoryId!.Value).Distinct().ToArray();
                var categoryNames = await db.Categories.AsNoTracking()
                    .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && categoryIds.Contains(x.Id))
                    .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

                foreach (var transaction in transactions)
                {
                    var userMapping = ResolveUserMapping(
                        mappingLookup,
                        mappingCategoryCodes,
                        TaxUserMappingTypes.TransactionSignature,
                        TaxLearningKey.Transaction(transaction.Counterparty, transaction.NormalizedCounterparty, transaction.Description));
                    if (userMapping.Ignore) continue;

                    var categoryName = transaction.CategoryId.HasValue && categoryNames.TryGetValue(transaction.CategoryId.Value, out var found)
                        ? found
                        : null;
                    var match = userMapping.Match ?? Evaluate(transaction.Counterparty, transaction.NormalizedCounterparty, transaction.Description, transaction.UserNote, categoryName);
                    if (match is null || match.Confidence < 0.40m) continue;

                    var outcome = await UpsertCandidateAsync(
                        fullWorthSpaceId,
                        profile.Id,
                        taxYear,
                        ruleVersion,
                        TaxSourceTypes.Transaction,
                        transaction.Id,
                        decimal.Abs(transaction.Amount),
                        transaction.Currency,
                        match,
                        Fingerprint(transaction.Id, transaction.UpdatedAt, ruleVersion),
                        [],
                        taxCategories,
                        ct);
                    created += outcome.Created ? 1 : 0;
                    changed += outcome.Changed ? 1 : 0;
                }
            }

            await db.SaveChangesAsync(ct);
            await CompleteRunAsync(run, sourcesAnalyzed, created, changed, ct);
            return new TaxAnalysisResult(true, taxYear, sourcesAnalyzed, created, changed, ruleVersion);
        }
        catch
        {
            run.Status = "failed";
            run.ErrorCode = "analysis_failed";
            run.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<UpsertOutcome> UpsertCandidateAsync(
        Guid fullWorthSpaceId,
        Guid taxProfileId,
        int taxYear,
        string ruleVersion,
        string sourceType,
        Guid sourceId,
        decimal grossAmount,
        string currency,
        RuleMatch match,
        string fingerprint,
        IReadOnlyCollection<SourceRef> additionalSources,
        IReadOnlyDictionary<string, Guid> taxCategories,
        CancellationToken ct)
    {
        if (grossAmount <= 0m || !taxCategories.TryGetValue(match.TaxCategoryCode, out var taxCategoryId))
            return UpsertOutcome.None;

        var existing = await (
            from source in db.TaxCandidateSources
            join candidate in db.TaxCandidates on source.TaxCandidateId equals candidate.Id
            where source.SourceType == sourceType
                  && source.SourceId == sourceId
                  && candidate.FullWorthSpaceId == fullWorthSpaceId
                  && candidate.TaxProfileId == taxProfileId
                  && candidate.TaxYear == taxYear
            select candidate).SingleOrDefaultAsync(ct);

        if (existing is null)
        {
            var eligiblePercentage = Math.Clamp(match.EligiblePercentage ?? 100m, 0m, 100m);
            var candidate = new TaxCandidate
            {
                FullWorthSpaceId = fullWorthSpaceId,
                TaxProfileId = taxProfileId,
                TaxYear = taxYear,
                Status = TaxCandidateStatuses.NeedsReview,
                TaxCategoryId = taxCategoryId,
                GrossAmount = grossAmount,
                EligibleAmount = decimal.Round(grossAmount * eligiblePercentage / 100m, 2, MidpointRounding.AwayFromZero),
                EligiblePercentage = eligiblePercentage,
                Currency = currency.ToUpperInvariant(),
                Confidence = match.Confidence,
                DetectionSource = match.DetectionSource,
                ReasonCode = match.ReasonCode,
                Explanation = match.Explanation,
                CountryCode = "DE",
                RuleVersion = ruleVersion,
                SourceFingerprint = fingerprint
            };
            db.TaxCandidates.Add(candidate);
            db.TaxCandidateSources.Add(new TaxCandidateSource
            {
                TaxCandidateId = candidate.Id,
                SourceType = sourceType,
                SourceId = sourceId,
                IsPrimary = true
            });
            foreach (var source in additionalSources.Distinct())
            {
                if (source.SourceType == sourceType && source.SourceId == sourceId) continue;
                db.TaxCandidateSources.Add(new TaxCandidateSource
                {
                    TaxCandidateId = candidate.Id,
                    SourceType = source.SourceType,
                    SourceId = source.SourceId,
                    IsPrimary = false
                });
            }
            return new UpsertOutcome(true, false, true);
        }

        await AddMissingSourcesAsync(existing.Id, sourceType, sourceId, additionalSources, ct);
        if (existing.SourceFingerprint == fingerprint)
            return new UpsertOutcome(false, false, true);
        if (existing.Status is TaxCandidateStatuses.Confirmed or TaxCandidateStatuses.Rejected or TaxCandidateStatuses.Ignored)
            return new UpsertOutcome(false, false, true);

        var updatedPercentage = Math.Clamp(match.EligiblePercentage ?? existing.EligiblePercentage, 0m, 100m);
        existing.TaxCategoryId = taxCategoryId;
        existing.GrossAmount = grossAmount;
        existing.EligiblePercentage = updatedPercentage;
        existing.EligibleAmount = decimal.Round(grossAmount * updatedPercentage / 100m, 2, MidpointRounding.AwayFromZero);
        existing.Currency = currency.ToUpperInvariant();
        existing.Confidence = match.Confidence;
        existing.DetectionSource = match.DetectionSource;
        existing.ReasonCode = match.ReasonCode;
        existing.Explanation = match.Explanation;
        existing.RuleVersion = ruleVersion;
        existing.SourceFingerprint = fingerprint;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return new UpsertOutcome(false, true, true);
    }

    private async Task AddMissingSourcesAsync(
        Guid candidateId,
        string primarySourceType,
        Guid primarySourceId,
        IReadOnlyCollection<SourceRef> additionalSources,
        CancellationToken ct)
    {
        if (additionalSources.Count == 0) return;
        var existing = await db.TaxCandidateSources.AsNoTracking()
            .Where(x => x.TaxCandidateId == candidateId)
            .Select(x => new SourceRef(x.SourceType, x.SourceId))
            .ToListAsync(ct);
        var existingSet = existing.ToHashSet();

        foreach (var source in additionalSources.Distinct())
        {
            if (source.SourceType == primarySourceType && source.SourceId == primarySourceId) continue;
            if (!existingSet.Add(source)) continue;
            db.TaxCandidateSources.Add(new TaxCandidateSource
            {
                TaxCandidateId = candidateId,
                SourceType = source.SourceType,
                SourceId = source.SourceId,
                IsPrimary = false
            });
        }
    }

    private IQueryable<Purchase> VisiblePurchases(Guid userId, Guid fullWorthSpaceId) =>
        db.Purchases.AsNoTracking().Where(purchase =>
            purchase.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == purchase.FullWorthSpaceId && member.UserId == userId) &&
            (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
            (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link => db.Transactions.Any(transaction =>
                transaction.Id == link.TransactionId && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == purchase.FullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId))))) &&
            (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                transaction.Id == purchase.TransactionId.Value && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == purchase.FullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

    private static IEnumerable<SourceRef> PurchaseEvidenceSources(Purchase purchase)
    {
        yield return new SourceRef(TaxSourceTypes.Purchase, purchase.Id);
        foreach (var document in purchase.Documents)
            yield return new SourceRef(TaxSourceTypes.PurchaseDocument, document.Id);
    }

    private static string PurchaseEvidenceVersion(Purchase purchase)
    {
        if (purchase.Documents.Count == 0) return purchase.UpdatedAt.ToString("O");
        var documents = string.Join('|', purchase.Documents
            .OrderBy(x => x.Id)
            .Select(x => $"{x.Id:N}:{x.UpdatedAt:O}:{x.Status}:{x.Sha256}"));
        return $"{purchase.UpdatedAt:O}|{documents}";
    }

    private static bool ShouldSkipItem(PurchaseItem item)
    {
        if (item.TotalPrice == 0m) return true;
        return item.LineType is "discount" or "deposit" or "rounding" or "subtotal" or "total";
    }

    private static UserMappingDecision ResolveUserMapping(
        IReadOnlyDictionary<(string MatchType, string MatchValue), TaxUserMapping> mappings,
        IReadOnlyDictionary<Guid, string> categoryCodes,
        string matchType,
        string matchValue)
    {
        if (string.IsNullOrWhiteSpace(matchValue) || !mappings.TryGetValue((matchType, matchValue), out var mapping))
            return UserMappingDecision.None;
        if (string.Equals(mapping.Action, "ignore", StringComparison.Ordinal))
            return new UserMappingDecision(true, null);
        if (!string.Equals(mapping.Action, "suggest", StringComparison.Ordinal) || !mapping.TaxCategoryId.HasValue ||
            !categoryCodes.TryGetValue(mapping.TaxCategoryId.Value, out var categoryCode))
            return UserMappingDecision.None;

        return new UserMappingDecision(false, new RuleMatch(
            categoryCode,
            0.95m,
            TaxDetectionSources.UserMapping,
            "user_mapping",
            "Ein gleichartiger Fall wurde zuvor von dir bestätigt.",
            mapping.EligiblePercentage));
    }

    private static RuleMatch? AddDocumentEvidence(RuleMatch? match, bool hasDocument)
    {
        if (match is null || !hasDocument || match.DetectionSource == TaxDetectionSources.UserMapping) return match;
        return match with
        {
            Confidence = Math.Min(0.95m, match.Confidence + 0.10m),
            DetectionSource = TaxDetectionSources.Combined,
            Explanation = match.Explanation + " Ein zugehöriger Beleg ist vorhanden."
        };
    }

    private static RuleMatch? Evaluate(params string?[] values)
    {
        var text = string.Join(' ', values.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return null;

        var candidates = new List<RuleMatch>();
        Add(candidates, text, DonationWords, "spenden", 0.78m, "donation_keyword", "Text oder Kategorie deutet auf eine mögliche Spende hin.");
        Add(candidates, text, EducationWords, "werbungskosten.fortbildung", 0.72m, "education_keyword", "Händler, Beschreibung oder Kategorie deutet auf Fort- oder Weiterbildung hin.");
        Add(candidates, text, SoftwareWords, "werbungskosten.software", 0.62m, "software_keyword", "Händler, Beschreibung oder Kategorie deutet auf Software oder einen digitalen Dienst hin.");
        Add(candidates, text, HandymanWords, "haushalt.handwerker", 0.58m, "handyman_keyword", "Beschreibung oder Kategorie deutet auf eine mögliche Handwerkerleistung hin.");
        Add(candidates, text, HouseholdServiceWords, "haushalt.nahe_dienstleistungen", 0.55m, "household_service_keyword", "Beschreibung oder Kategorie deutet auf eine mögliche haushaltsnahe Dienstleistung hin.");
        Add(candidates, text, InsuranceWords, "sonderausgaben.versicherungen", 0.50m, "insurance_keyword", "Beschreibung oder Kategorie deutet auf einen Versicherungsbeitrag hin; die steuerliche Behandlung muss geprüft werden.");
        Add(candidates, text, WorkEquipmentWords, "werbungskosten.arbeitsmittel", 0.48m, "work_equipment_keyword", "Beschreibung oder Kategorie deutet auf ein mögliches Arbeitsmittel hin.");

        if (candidates.Count == 0) return null;
        var best = candidates.OrderByDescending(x => x.Confidence).First();
        return candidates.Count == 1
            ? best
            : best with
            {
                Confidence = Math.Min(0.95m, best.Confidence + 0.08m),
                DetectionSource = TaxDetectionSources.Combined,
                Explanation = best.Explanation + " Mehrere Hinweise wurden kombiniert."
            };
    }

    private static void Add(List<RuleMatch> matches, string text, IEnumerable<string> words, string taxCategoryCode, decimal score, string reasonCode, string explanation)
    {
        if (words.Any(word => text.Contains(word, StringComparison.Ordinal)))
            matches.Add(new RuleMatch(taxCategoryCode, score, TaxDetectionSources.Keyword, reasonCode, explanation, null));
    }

    private async Task CompleteRunAsync(TaxAnalysisRun run, int sourcesAnalyzed, int created, int changed, CancellationToken ct)
    {
        run.SourcesAnalyzed = sourcesAnalyzed;
        run.CandidatesCreated = created;
        run.CandidatesChanged = changed;
        run.Status = "completed";
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string RuleVersion(string countryCode, int taxYear) =>
        string.Equals(countryCode, "DE", StringComparison.OrdinalIgnoreCase)
            ? GermanyTaxCatalog.RuleVersion(taxYear)
            : $"{countryCode.ToUpperInvariant()}-{taxYear}-unsupported";

    private static string Fingerprint(Guid sourceId, DateTimeOffset updatedAt, string ruleVersion, string? evidenceVersion = null)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId:N}|{updatedAt:O}|{ruleVersion}|{evidenceVersion}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record RuleMatch(
        string TaxCategoryCode,
        decimal Confidence,
        string DetectionSource,
        string ReasonCode,
        string Explanation,
        decimal? EligiblePercentage);

    private sealed record SourceRef(string SourceType, Guid SourceId);
    private sealed record UserMappingDecision(bool Ignore, RuleMatch? Match)
    {
        internal static readonly UserMappingDecision None = new(false, null);
    }
    private sealed record UpsertOutcome(bool Created, bool Changed, bool Surfaced)
    {
        internal static readonly UpsertOutcome None = new(false, false, false);
    }
}
