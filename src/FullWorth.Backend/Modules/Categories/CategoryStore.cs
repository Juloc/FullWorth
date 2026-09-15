using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

public sealed class CategoryStore(FullWorthDbContext db, AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    public Task<List<FinanceCategory>> ListAsync(CancellationToken ct) => ListForSpaceAsync(FullWorthSpaceDefaults.LegacyId, ct);
    public Task<List<CategorizationRule>> ListRulesAsync(CancellationToken ct) => ListRulesForSpaceAsync(FullWorthSpaceDefaults.LegacyId, ct);

    public Task<List<FinanceCategory>> ListForSpaceAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        ListForSpaceAsync(fullWorthSpaceId, includeArchived: true, ct);

    public Task<List<FinanceCategory>> ListForSpaceAsync(Guid fullWorthSpaceId, bool includeArchived, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .Where(x => includeArchived || !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(ct);

    public Task<List<CategorizationRule>> ListRulesForSpaceAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.CategorizationRules.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(x => x.Target).ThenBy(x => x.Priority).ThenBy(x => x.Name)
            .ToListAsync(ct);

    public async Task<(bool Found, List<FinanceCategory>? Items)> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct, bool includeArchived = false)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (false, null);
        return (true, await ListForSpaceAsync(fullWorthSpaceId, includeArchived, ct));
    }

    public async Task<(bool Found, List<CategorizationRule>? Items)> ListRulesForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        // Reading the space's categorization rules is a configuration read, gated on membership only —
        // exactly like listing categories. The transactions.categorize capability is required to CREATE,
        // UPDATE, reapply or preview rules, not to view them.
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (false, null);
        return (true, await ListRulesForSpaceAsync(fullWorthSpaceId, ct));
    }

    public Task<FinanceCategory> CreateAsync(CategoryWrite request, CancellationToken ct) =>
        CreateForSpaceAsync(FullWorthSpaceDefaults.LegacyId, request, ct);

    public async Task<FinanceCategory> CreateForSpaceAsync(Guid fullWorthSpaceId, CategoryWrite request, CancellationToken ct)
    {
        await ValidateParentAsync(fullWorthSpaceId, request.ParentId, ct);
        ValidateCategoryWrite(request);
        var entity = NewCategory(fullWorthSpaceId, request);
        db.Categories.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity!;
    }

    public async Task<CategoryMutationOutcome<FinanceCategory>> CreateForUserAsync(Guid userId, Guid fullWorthSpaceId, CategoryWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.NotFound);
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return new(CategoryMutationResult.Forbidden);
        if (request.ParentId.HasValue && !await CategoryExistsAsync(fullWorthSpaceId, request.ParentId.Value, ct))
            return new(CategoryMutationResult.NotFound);

        try
        {
            ValidateCategoryWrite(request);
            var entity = NewCategory(fullWorthSpaceId, request);
            db.Categories.Add(entity);
            audit.Record(fullWorthSpaceId, userId, "category.created", "FinanceCategory", entity.Id);
            await db.SaveChangesAsync(ct);
            return new(CategoryMutationResult.Success, entity);
        }
        catch (ArgumentException exception)
        {
            return new(CategoryMutationResult.Invalid, Error: exception.Message);
        }
    }

    public async Task<CategoryMutationOutcome<FinanceCategory>> UpdateForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid categoryId, CategoryUpdate request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.NotFound);
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return new(CategoryMutationResult.Forbidden);

        var category = await db.Categories.SingleOrDefaultAsync(x => x.Id == categoryId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (category is null) return new(CategoryMutationResult.NotFound);
        if (string.IsNullOrWhiteSpace(request.Name)) return new(CategoryMutationResult.Invalid, Error: "Category name is required.");

        if (request.ParentId != category.ParentId)
        {
            if (request.ParentId.HasValue)
            {
                if (request.ParentId.Value == categoryId)
                    return new(CategoryMutationResult.Invalid, Error: "A category cannot be its own parent.");
                if (!await CategoryExistsAsync(fullWorthSpaceId, request.ParentId.Value, ct))
                    return new(CategoryMutationResult.NotFound);
                if (await WouldCreateCycleAsync(fullWorthSpaceId, categoryId, request.ParentId.Value, ct))
                    return new(CategoryMutationResult.Invalid, Error: "A category cannot be moved under one of its own descendants.");
            }
            category.ParentId = request.ParentId;
        }

        category.Name = request.Name.Trim();
        category.Icon = request.Icon?.Trim();
        if (request.SortOrder.HasValue) category.SortOrder = request.SortOrder.Value;
        audit.Record(fullWorthSpaceId, userId, "category.updated", "FinanceCategory", category.Id);
        await db.SaveChangesAsync(ct);
        return new(CategoryMutationResult.Success, category);
    }

    public async Task<CategoryMutationOutcome<FinanceCategory>> ArchiveForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.NotFound);
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return new(CategoryMutationResult.Forbidden);

        var category = await db.Categories.SingleOrDefaultAsync(x => x.Id == categoryId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (category is null) return new(CategoryMutationResult.NotFound);
        if (category.IsArchived) return new(CategoryMutationResult.Success, category);

        var hasActiveChildren = await db.Categories.AsNoTracking()
            .AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.ParentId == categoryId && !x.IsArchived, ct);
        if (hasActiveChildren)
            return new(CategoryMutationResult.Invalid, Error: "Archive or reparent child categories first.");

        category.IsArchived = true;
        audit.Record(fullWorthSpaceId, userId, "category.archived", "FinanceCategory", category.Id);
        await db.SaveChangesAsync(ct);
        return new(CategoryMutationResult.Success, category);
    }

    public async Task<CategoryMutationOutcome<FinanceCategory>> UnarchiveForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.NotFound);
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return new(CategoryMutationResult.Forbidden);

        var category = await db.Categories.SingleOrDefaultAsync(x => x.Id == categoryId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (category is null) return new(CategoryMutationResult.NotFound);
        if (!category.IsArchived) return new(CategoryMutationResult.Success, category);

        if (category.ParentId is { } parentId)
        {
            var parentArchived = await db.Categories.AsNoTracking()
                .AnyAsync(x => x.Id == parentId && x.FullWorthSpaceId == fullWorthSpaceId && x.IsArchived, ct);
            if (parentArchived)
                return new(CategoryMutationResult.Invalid, Error: "Restore the parent category first.");
        }

        category.IsArchived = false;
        audit.Record(fullWorthSpaceId, userId, "category.unarchived", "FinanceCategory", category.Id);
        await db.SaveChangesAsync(ct);
        return new(CategoryMutationResult.Success, category);
    }

    private async Task<bool> WouldCreateCycleAsync(Guid fullWorthSpaceId, Guid categoryId, Guid newParentId, CancellationToken ct)
    {
        var pairs = await db.Categories.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => new { x.Id, x.ParentId })
            .ToListAsync(ct);
        var parentOf = pairs.ToDictionary(x => x.Id, x => x.ParentId);

        var cursor = (Guid?)newParentId;
        var guard = 0;
        while (cursor.HasValue && guard++ <= pairs.Count)
        {
            if (cursor.Value == categoryId) return true;
            cursor = parentOf.TryGetValue(cursor.Value, out var parent) ? parent : null;
        }
        return false;
    }

    public Task<CategorizationRule> UpsertRuleAsync(Guid? id, RuleWrite request, CancellationToken ct) =>
        UpsertRuleForSpaceAsync(FullWorthSpaceDefaults.LegacyId, id, request, ct);

    public async Task<CategorizationRule> UpsertRuleForSpaceAsync(Guid fullWorthSpaceId, Guid? id, RuleWrite request, CancellationToken ct)
    {
        if (!await CategoryExistsAsync(fullWorthSpaceId, request.CategoryId, ct))
            throw new InvalidOperationException("Categorization rule category must belong to the same FullWorth Space.");

        var entity = id.HasValue
            ? await db.CategorizationRules.SingleOrDefaultAsync(x => x.Id == id.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct)
            : null;
        if (id.HasValue && entity is null) throw new InvalidOperationException("Categorization rule not found in FullWorth Space.");
        var isNew = entity is null;
        if (isNew)
        {
            entity = new CategorizationRule { FullWorthSpaceId = fullWorthSpaceId };
            db.CategorizationRules.Add(entity);
        }
        ApplyRuleWrite(entity!, request);
        await db.SaveChangesAsync(ct);
        return entity!;
    }

    public async Task<CategoryMutationOutcome<CategorizationRule>> UpsertRuleForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid? id,
        RuleWrite request,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.NotFound);
        if (!await CanManageGlobalRulesAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.Forbidden);
        if (!await CategoryExistsAsync(fullWorthSpaceId, request.CategoryId, ct))
            return new(CategoryMutationResult.NotFound);

        var entity = id.HasValue
            ? await db.CategorizationRules.SingleOrDefaultAsync(x => x.Id == id.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct)
            : null;
        if (id.HasValue && entity is null) return new(CategoryMutationResult.NotFound);
        var isNew = entity is null;
        if (isNew)
        {
            entity = new CategorizationRule { FullWorthSpaceId = fullWorthSpaceId };
            db.CategorizationRules.Add(entity);
        }

        try
        {
            ValidateRuleWrite(request);
            ApplyRuleWrite(entity!, request);
            audit.Record(fullWorthSpaceId, userId, isNew ? "category.rule.created" : "category.rule.updated", "CategorizationRule", entity!.Id);
            await db.SaveChangesAsync(ct);
            return new(CategoryMutationResult.Success, entity);
        }
        catch (ArgumentException exception)
        {
            return new(CategoryMutationResult.Invalid, Error: exception.Message);
        }
    }

    public async Task<CategoryMutationOutcome<ReapplyResult>> ReapplyRulesForUserAsync(Guid userId, Guid fullWorthSpaceId, bool apply, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.NotFound);
        if (!await CanManageGlobalRulesAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.Forbidden);

        var rules = await db.CategorizationRules.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.IsEnabled && x.Target == "transaction")
            .OrderBy(x => x.Priority).ThenBy(x => x.Id)
            .ToListAsync(ct);
        var activeCategoryRows = await db.Categories.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && !x.IsArchived)
            .Select(x => new { x.Key, x.Id })
            .ToListAsync(ct);
        var activeCategoryIdsByKey = activeCategoryRows
            .ToDictionary(x => x.Key, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var writable = await RawSql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);

        var query = db.Transactions.Where(t =>
            writable.Contains(t.AccountId) && t.CategorizationSource != "manual");
        var transactions = apply ? await query.ToListAsync(ct) : await query.AsNoTracking().ToListAsync(ct);

        var changed = 0;
        foreach (var tx in transactions)
        {
            var classification = TransactionRuleEngine.EvaluateWithGermanyCatalog(tx, rules, activeCategoryIdsByKey);
            if (classification.CategoryId == tx.CategoryId &&
                classification.IsTransfer == tx.IsTransfer &&
                classification.Source == tx.CategorizationSource)
                continue;

            changed++;
            if (apply)
            {
                tx.CategoryId = classification.CategoryId;
                tx.IsTransfer = classification.IsTransfer;
                tx.CategorizationSource = classification.Source;
            }
        }

        if (apply && changed > 0)
        {
            audit.Record(fullWorthSpaceId, userId, "category.rules.reapplied", "CategorizationRule");
            await db.SaveChangesAsync(ct);
        }
        return new(CategoryMutationResult.Success, new ReapplyResult(transactions.Count, changed, apply));
    }

    public async Task<CategoryMutationOutcome<RulePreviewResult>> PreviewRuleForUserAsync(Guid userId, Guid fullWorthSpaceId, RuleWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.NotFound);
        if (!await CanManageGlobalRulesAsync(userId, fullWorthSpaceId, ct)) return new(CategoryMutationResult.Forbidden);

        var hasCondition = !string.IsNullOrWhiteSpace(request.Pattern)
            || request.MinAmount.HasValue || request.MaxAmount.HasValue
            || !string.IsNullOrWhiteSpace(request.MerchantCategoryCode)
            || (!string.IsNullOrWhiteSpace(request.Direction) && request.Direction.Trim().ToLowerInvariant() != "any");
        if (!hasCondition) return new(CategoryMutationResult.Invalid, Error: "Add at least one condition to preview.");

        var draft = new CategorizationRule { FullWorthSpaceId = fullWorthSpaceId };
        ApplyRuleWrite(draft, request with { Name = string.IsNullOrWhiteSpace(request.Name) ? "draft" : request.Name });

        const int scanCap = 5000;
        var writable = await RawSql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var candidates = await db.Transactions.AsNoTracking()
            .Where(t => writable.Contains(t.AccountId))
            .OrderByDescending(t => t.BookingDate ?? t.ValueDate)
            .Take(scanCap)
            .Select(t => new { t.Id, t.BookingDate, t.ValueDate, t.Amount, t.Currency, t.Counterparty, t.NormalizedCounterparty, t.Description, t.MerchantCategoryCode, t.CategoryId })
            .ToListAsync(ct);

        var matched = candidates.Where(c => TransactionRuleEngine.MatchesRule(
            new Transactions.FinanceTransaction
            {
                Amount = c.Amount,
                Counterparty = c.Counterparty,
                NormalizedCounterparty = c.NormalizedCounterparty,
                Description = c.Description,
                MerchantCategoryCode = c.MerchantCategoryCode
            }, draft)).ToList();

        var sampleSrc = matched.Take(15).ToList();
        var catIds = sampleSrc.Where(x => x.CategoryId.HasValue).Select(x => x.CategoryId!.Value).Distinct().ToList();
        var catNames = await db.Categories.AsNoTracking().Where(c => catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var sample = sampleSrc.Select(x => new RulePreviewMatch(
            x.Id,
            x.BookingDate ?? x.ValueDate,
            string.IsNullOrWhiteSpace(x.Counterparty) ? x.Description : x.Counterparty,
            x.Amount,
            x.Currency,
            x.CategoryId.HasValue && catNames.TryGetValue(x.CategoryId.Value, out var n) ? n : null)).ToList();

        return new(CategoryMutationResult.Success, new RulePreviewResult(candidates.Count, matched.Count, sample, candidates.Count >= scanCap));
    }

    public Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);

    private async Task<bool> CanManageGlobalRulesAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return false;
        var activeAccountIds = await db.Accounts.AsNoTracking()
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.IsActive)
            .Select(account => account.Id)
            .ToListAsync(ct);
        var writable = await RawSql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        return activeAccountIds.All(writable.Contains);
    }

    private Task<bool> CategoryExistsAsync(Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking().AnyAsync(x => x.Id == categoryId && x.FullWorthSpaceId == fullWorthSpaceId, ct);

    private Task ValidateParentAsync(Guid fullWorthSpaceId, Guid? parentId, CancellationToken ct)
    {
        if (!parentId.HasValue) return Task.CompletedTask;
        return ValidateParentExistsAsync(fullWorthSpaceId, parentId.Value, ct);
    }

    private async Task ValidateParentExistsAsync(Guid fullWorthSpaceId, Guid parentId, CancellationToken ct)
    {
        if (!await CategoryExistsAsync(fullWorthSpaceId, parentId, ct))
            throw new InvalidOperationException("Parent category must belong to the same FullWorth Space.");
    }

    private static FinanceCategory NewCategory(Guid fullWorthSpaceId, CategoryWrite request) => new()
    {
        FullWorthSpaceId = fullWorthSpaceId,
        Key = request.Key.Trim().ToLowerInvariant(),
        Name = request.Name.Trim(),
        ParentId = request.ParentId,
        Icon = request.Icon?.Trim(),
        SortOrder = request.SortOrder ?? 500
    };

    private static void ValidateCategoryWrite(CategoryWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Key)) throw new ArgumentException("Category key is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Category name is required.");
    }

    private static void ValidateRuleWrite(RuleWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Rule name is required.");
        if (string.IsNullOrWhiteSpace(request.MatchField)) throw new ArgumentException("Rule match field is required.");
        if (string.IsNullOrWhiteSpace(request.MatchMode)) throw new ArgumentException("Rule match mode is required.");
        if (string.IsNullOrWhiteSpace(request.Direction)) throw new ArgumentException("Rule direction is required.");
    }

    private static void ApplyRuleWrite(CategorizationRule entity, RuleWrite request)
    {
        entity.Name = request.Name.Trim();
        entity.IsEnabled = request.IsEnabled;
        entity.Priority = request.Priority;
        entity.Target = string.IsNullOrWhiteSpace(request.Target) ? "transaction" : request.Target.Trim().ToLowerInvariant();
        entity.MatchField = request.MatchField.Trim().ToLowerInvariant();
        entity.MatchMode = request.MatchMode.Trim().ToLowerInvariant();
        entity.Pattern = request.Pattern.Trim();
        entity.Direction = request.Direction.Trim().ToLowerInvariant();
        entity.MinAmount = request.MinAmount;
        entity.MaxAmount = request.MaxAmount;
        entity.MerchantCategoryCode = request.MerchantCategoryCode?.Trim();
        entity.CategoryId = request.CategoryId;
        entity.MarkAsTransfer = request.MarkAsTransfer;
        entity.StopProcessing = request.StopProcessing;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
