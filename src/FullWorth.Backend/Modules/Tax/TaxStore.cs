using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Tax;

public sealed class TaxStore(FullWorthDbContext db, AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);

    public async Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);

    public async Task<bool> IsOwnerAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId && x.Role == FullWorthSpaceRoles.Owner, ct);

    public async Task<TaxSettings?> GetSettingsAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var settings = await db.TaxSettings.SingleOrDefaultAsync(x => x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (settings is not null) return settings;

        settings = new TaxSettings { FullWorthSpaceId = fullWorthSpaceId };
        db.TaxSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<(bool Found, bool Forbidden, TaxSettings? Value)> UpdateSettingsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        TaxSettingsUpdateRequest request,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (false, false, null);
        if (!await IsOwnerAsync(userId, fullWorthSpaceId, ct)) return (true, true, null);

        var settings = await db.TaxSettings.SingleOrDefaultAsync(x => x.FullWorthSpaceId == fullWorthSpaceId, ct)
            ?? new TaxSettings { FullWorthSpaceId = fullWorthSpaceId };
        if (db.Entry(settings).State == EntityState.Detached) db.TaxSettings.Add(settings);
        var wasEnabled = settings.Enabled;

        settings.Enabled = request.Enabled;
        settings.CountryCode = request.CountryCode.Trim().ToUpperInvariant();
        settings.DefaultTaxYear = request.DefaultTaxYear;
        settings.AutomaticAnalysisEnabled = request.AutomaticAnalysisEnabled;
        settings.AiAnalysisEnabled = request.AiAnalysisEnabled;
        settings.AnalyzeTransactions = request.AnalyzeTransactions;
        settings.AnalyzePurchases = request.AnalyzePurchases;
        settings.AnalyzeDocuments = request.AnalyzeDocuments;
        settings.ShowTaxNotifications = request.ShowTaxNotifications;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(
            fullWorthSpaceId,
            userId,
            wasEnabled == settings.Enabled ? "tax.settings.updated" : settings.Enabled ? "tax.enabled" : "tax.disabled",
            "TaxSettings",
            settings.Id);
        await db.SaveChangesAsync(ct);
        return (true, false, settings);
    }

    public async Task<TaxProfile?> EnsurePersonalProfileAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;

        var existing = await db.TaxProfiles
            .SingleOrDefaultAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);
        if (existing is not null) return existing;

        var settings = await db.TaxSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.FullWorthSpaceId == fullWorthSpaceId, ct);
        var displayName = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => x.DisplayName)
            .SingleAsync(ct);

        var profile = new TaxProfile
        {
            FullWorthSpaceId = fullWorthSpaceId,
            UserId = userId,
            DisplayName = displayName,
            CountryCode = settings?.CountryCode ?? "DE"
        };
        db.TaxProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<TaxProfile?> UpdatePersonalProfileSettingsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        TaxProfileSettingsUpdateRequest request,
        CancellationToken ct)
    {
        var profile = await EnsurePersonalProfileAsync(userId, fullWorthSpaceId, ct);
        if (profile is null) return null;
        if (profile.AssistantEnabled == request.AssistantEnabled) return profile;

        profile.AssistantEnabled = request.AssistantEnabled;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(
            fullWorthSpaceId,
            userId,
            request.AssistantEnabled ? "tax.profile.enabled" : "tax.profile.disabled",
            "TaxProfile",
            profile.Id);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<List<TaxProfile>?> ListProfilesAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsurePersonalProfileAsync(userId, fullWorthSpaceId, ct);
        var isOwner = await IsOwnerAsync(userId, fullWorthSpaceId, ct);

        var query = db.TaxProfiles.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.Active);
        if (!isOwner) query = query.Where(x => x.UserId == userId);

        return await query.OrderBy(x => x.DisplayName).ToListAsync(ct);
    }

    public async Task EnsureGermanCatalogAsync(CancellationToken ct)
    {
        var codes = GermanyTaxCatalog.Definitions.Select(x => x.Code).ToArray();
        var existing = await db.TaxCategories
            .Where(x => x.CountryCode == "DE" && x.ValidFromTaxYear == GermanyTaxCatalog.FirstSupportedYear && codes.Contains(x.Code))
            .Select(x => x.Code)
            .ToListAsync(ct);
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);

        foreach (var item in GermanyTaxCatalog.Definitions)
        {
            if (existingSet.Contains(item.Code)) continue;
            db.TaxCategories.Add(new TaxCategory
            {
                CountryCode = "DE",
                Code = item.Code,
                ParentCode = item.ParentCode,
                Name = item.Name,
                Description = item.Description,
                ValidFromTaxYear = GermanyTaxCatalog.FirstSupportedYear,
                Active = true
            });
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    public async Task<List<TaxCategory>?> ListCategoriesAsync(Guid userId, Guid fullWorthSpaceId, int taxYear, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        await EnsureGermanCatalogAsync(ct);
        var country = await db.TaxSettings.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => x.CountryCode)
            .SingleOrDefaultAsync(ct) ?? "DE";

        return await db.TaxCategories.AsNoTracking()
            .Where(x => x.CountryCode == country && x.Active && x.ValidFromTaxYear <= taxYear && (!x.ValidUntilTaxYear.HasValue || x.ValidUntilTaxYear >= taxYear))
            .OrderBy(x => x.ParentCode).ThenBy(x => x.Code)
            .ToListAsync(ct);
    }

    public async Task<List<TaxCandidate>?> ListCandidatesAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        int taxYear,
        string? status,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var isOwner = await IsOwnerAsync(userId, fullWorthSpaceId, ct);
        var q = AccessibleCandidates(userId, fullWorthSpaceId, isOwner)
            .AsNoTracking()
            .Where(x => x.TaxYear == taxYear);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        return await q.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.UpdatedAt).ToListAsync(ct);
    }

    public async Task<TaxCandidate?> GetCandidateAsync(Guid userId, Guid fullWorthSpaceId, Guid candidateId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var isOwner = await IsOwnerAsync(userId, fullWorthSpaceId, ct);
        return await AccessibleCandidates(userId, fullWorthSpaceId, isOwner)
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == candidateId, ct);
    }

    public async Task<(bool Found, TaxCandidate? Value)> UpdateCandidateAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid candidateId,
        TaxCandidateUpdateRequest request,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (false, null);
        var isOwner = await IsOwnerAsync(userId, fullWorthSpaceId, ct);
        var candidate = await AccessibleCandidates(userId, fullWorthSpaceId, isOwner)
            .SingleOrDefaultAsync(x => x.Id == candidateId, ct);
        if (candidate is null) return (false, null);

        var oldStatus = candidate.Status;
        var oldCategory = candidate.TaxCategoryId;

        if (request.TaxCategoryId.HasValue)
        {
            var categoryExists = await db.TaxCategories.AsNoTracking()
                .AnyAsync(x => x.Id == request.TaxCategoryId.Value && x.CountryCode == candidate.CountryCode, ct);
            if (!categoryExists) return (false, null);
            candidate.TaxCategoryId = request.TaxCategoryId.Value;
        }

        if (request.EligiblePercentage.HasValue)
        {
            candidate.EligiblePercentage = Math.Clamp(request.EligiblePercentage.Value, 0m, 100m);
            candidate.EligibleAmount = decimal.Round(candidate.GrossAmount * candidate.EligiblePercentage / 100m, 2, MidpointRounding.AwayFromZero);
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!TaxCandidateStatuses.IsValid(request.Status)) return (false, null);
            candidate.Status = request.Status;
        }

        candidate.UpdatedAt = DateTimeOffset.UtcNow;
        if (candidate.Status is TaxCandidateStatuses.Confirmed or TaxCandidateStatuses.Rejected or TaxCandidateStatuses.Ignored)
        {
            candidate.ReviewedAt = DateTimeOffset.UtcNow;
            candidate.ReviewedByUserId = userId;
        }

        db.TaxFeedback.Add(new TaxFeedback
        {
            FullWorthSpaceId = fullWorthSpaceId,
            TaxCandidateId = candidate.Id,
            UserId = userId,
            OriginalStatus = oldStatus,
            OriginalCategoryId = oldCategory,
            Decision = candidate.Status,
            NewCategoryId = candidate.TaxCategoryId,
            NewEligiblePercentage = candidate.EligiblePercentage
        });
        audit.Record(
            fullWorthSpaceId,
            userId,
            candidate.Status switch
            {
                TaxCandidateStatuses.Confirmed => "tax.candidate.confirmed",
                TaxCandidateStatuses.Rejected => "tax.candidate.rejected",
                TaxCandidateStatuses.Ignored => "tax.candidate.ignored",
                _ => "tax.candidate.edited"
            },
            "TaxCandidate",
            candidate.Id);

        await db.SaveChangesAsync(ct);
        await new TaxLearningService(db).LearnFromDecisionAsync(userId, candidate, ct);
        return (true, candidate);
    }

    public async Task<TaxSummaryView?> GetSummaryAsync(Guid userId, Guid fullWorthSpaceId, int taxYear, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var isOwner = await IsOwnerAsync(userId, fullWorthSpaceId, ct);
        var q = AccessibleCandidates(userId, fullWorthSpaceId, isOwner)
            .AsNoTracking()
            .Where(x => x.TaxYear == taxYear);
        var suggested = await q.Where(x => x.Status != TaxCandidateStatuses.Rejected && x.Status != TaxCandidateStatuses.Ignored)
            .SumAsync(x => (decimal?)x.EligibleAmount, ct) ?? 0m;
        var confirmed = await q.Where(x => x.Status == TaxCandidateStatuses.Confirmed)
            .SumAsync(x => (decimal?)x.EligibleAmount, ct) ?? 0m;
        var needsReview = await q.CountAsync(x => x.Status == TaxCandidateStatuses.NeedsReview || x.Status == TaxCandidateStatuses.Detected, ct);
        var needsDocument = await q.CountAsync(x => x.Status == TaxCandidateStatuses.NeedsDocument, ct);
        var confirmedCount = await q.CountAsync(x => x.Status == TaxCandidateStatuses.Confirmed, ct);
        return new TaxSummaryView(taxYear, suggested, confirmed, needsReview, needsDocument, confirmedCount);
    }

    public async Task<(bool Found, bool Forbidden)> DeleteTaxDataAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (false, false);
        if (!await IsOwnerAsync(userId, fullWorthSpaceId, ct)) return (true, true);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var candidateIds = db.TaxCandidates.Where(x => x.FullWorthSpaceId == fullWorthSpaceId).Select(x => x.Id);
        await db.TaxFeedback.Where(x => x.FullWorthSpaceId == fullWorthSpaceId).ExecuteDeleteAsync(ct);
        await db.TaxCandidateSources.Where(x => candidateIds.Contains(x.TaxCandidateId)).ExecuteDeleteAsync(ct);
        await db.TaxUserMappings.Where(x => x.FullWorthSpaceId == fullWorthSpaceId).ExecuteDeleteAsync(ct);
        await db.TaxCandidates.Where(x => x.FullWorthSpaceId == fullWorthSpaceId).ExecuteDeleteAsync(ct);
        await db.TaxAnalysisRuns.Where(x => x.FullWorthSpaceId == fullWorthSpaceId).ExecuteDeleteAsync(ct);
        await db.TaxProfiles.Where(x => x.FullWorthSpaceId == fullWorthSpaceId).ExecuteDeleteAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "tax.data.deleted", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (true, false);
    }

    private IQueryable<TaxCandidate> AccessibleCandidates(Guid userId, Guid fullWorthSpaceId, bool isOwner)
    {
        var query = db.TaxCandidates.Where(x => x.FullWorthSpaceId == fullWorthSpaceId);
        if (!isOwner)
        {
            query = query.Where(candidate => db.TaxProfiles.Any(profile =>
                profile.Id == candidate.TaxProfileId &&
                profile.FullWorthSpaceId == fullWorthSpaceId &&
                profile.UserId == userId));
        }
        return query;
    }
}
