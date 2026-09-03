using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

public sealed record CategoryExplanation(decimal Confidence, string ReasonCode, string? Detail);
public sealed record ReviewWrite(IReadOnlyList<Guid>? TransactionIds, bool IsReviewed);
public sealed record BulkCategoryAction(
    IReadOnlyList<Guid>? TransactionIds,
    bool UpdateCategory = false,
    Guid? CategoryId = null,
    bool? IsIgnored = null,
    bool? IsReviewed = null,
    IReadOnlyList<Guid>? AddTagIds = null,
    IReadOnlyList<Guid>? RemoveTagIds = null);
public sealed record LearnCategoryWrite(Guid TransactionId, Guid CategoryId, string Scope);
public sealed record TagWrite(string Name, string? Color);
public sealed record TagAssignmentWrite(IReadOnlyList<Guid>? TagIds);
public sealed record CategoryAppearanceWrite(string? Color);
public sealed record IntelligenceTag(Guid Id, string Name, string? Color);
public sealed record CategoryAppearanceView(Guid CategoryId, string? Color);

/// <summary>
/// Deterministic evidence strength for explainable categorization. Confidence communicates the
/// strength of the evidence, not a statistical or ML probability.
/// </summary>
public static class CategoryIntelligenceExplanation
{
    public static CategoryExplanation Explain(FinanceTransaction transaction, IReadOnlyList<CategorizationRule> rules)
    {
        var source = transaction.CategorizationSource?.Trim().ToLowerInvariant() ?? "none";
        if (source == "manual") return new(1.00m, "manual", null);

        if (source == "rule")
        {
            CategorizationRule? matched = null;
            foreach (var rule in rules)
            {
                if (!TransactionRuleEngine.MatchesRule(transaction, rule)) continue;
                matched = rule;
                if (rule.StopProcessing) break;
            }
            return new(0.99m, "rule", matched?.Name);
        }

        if (source == "catalog")
        {
            var match = GermanyCategorizationCatalog.Classify(transaction);
            if (match is null) return new(0.75m, "catalog", null);
            var reason = match.Value.Reason;
            if (reason.StartsWith("merchant:", StringComparison.OrdinalIgnoreCase))
                return new(0.97m, "merchant", reason[9..]);
            if (reason.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
                return new(0.90m, "text", reason[5..]);
            if (reason.StartsWith("mcc:", StringComparison.OrdinalIgnoreCase))
                return new(0.78m, "mcc", reason[4..]);
            return new(0.80m, "catalog", reason);
        }

        if (transaction.CategoryId.HasValue && source != "none")
            return new(0.95m, "imported", transaction.CategorizationSource);

        return new(0m, "unclassified", null);
    }
}

internal static class CategoryIntelligenceStore
{
    private sealed record ReviewState(bool IsReviewed);
    private sealed record Appearance(Guid CategoryId, string? Color);

    public static async Task<object?> OverviewAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsSpaceMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;

        var transactions = await AccessibleTransactions(db, userId, fullWorthSpaceId, ownerOnly: false)
            .AsNoTracking()
            .OrderByDescending(x => x.BookingDate)
            .ThenByDescending(x => x.UpdatedAt)
            .Take(5000)
            .ToListAsync(ct);
        var rules = await db.CategorizationRules.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.IsEnabled && x.Target == "transaction")
            .OrderBy(x => x.Priority).ThenBy(x => x.Id)
            .ToListAsync(ct);
        var reviews = await LoadReviewsAsync(db, fullWorthSpaceId, ct);
        var transactionTags = await LoadTransactionTagsAsync(db, fullWorthSpaceId, ct);
        var appearanceByCategory = (await LoadAppearancesAsync(db, fullWorthSpaceId, ct))
            .ToDictionary(x => x.CategoryId, x => x.Color);

        var repeatedManual = transactions
            .Where(x => x.CategoryId.HasValue && x.CategorizationSource == "manual")
            .Select(x => new
            {
                Merchant = MerchantNormalization.Normalize(x.NormalizedCounterparty ?? x.Counterparty),
                CategoryId = x.CategoryId!.Value,
                Direction = Math.Sign(x.Amount)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Merchant))
            .GroupBy(x => (x.Merchant!, x.CategoryId, x.Direction))
            .Where(x => x.Count() >= 3)
            .Select(x => x.Key)
            .ToHashSet();

        var items = transactions.Select(tx =>
        {
            var explanation = CategoryIntelligenceExplanation.Explain(tx, rules);
            var source = tx.CategorizationSource?.Trim().ToLowerInvariant() ?? "none";
            var isExternalClassification = tx.CategoryId.HasValue && source != "none" && source != "rule" && source != "catalog" && source != "manual";
            var defaultReviewed = source == "manual" || isExternalClassification;
            var isReviewed = reviews.TryGetValue(tx.Id, out var explicitState) ? explicitState.IsReviewed : defaultReviewed;
            var merchant = MerchantNormalization.Normalize(tx.NormalizedCounterparty ?? tx.Counterparty);
            var learningSuggested = tx.CategoryId.HasValue && !string.IsNullOrWhiteSpace(merchant) &&
                repeatedManual.Contains((merchant!, tx.CategoryId.Value, Math.Sign(tx.Amount)));
            var tagsForTransaction = transactionTags.TryGetValue(tx.Id, out var linkedTags)
                ? linkedTags
                : Array.Empty<IntelligenceTag>();
            var categoryColor = tx.CategoryId.HasValue && appearanceByCategory.TryGetValue(tx.CategoryId.Value, out var explicitColor)
                ? explicitColor
                : null;

            return new
            {
                tx.Id,
                tx.CategoryId,
                tx.CategorizationSource,
                isReviewed,
                needsReview = !isReviewed,
                explanation.Confidence,
                explanation.ReasonCode,
                explanation.Detail,
                learningSuggested,
                categoryColor,
                tags = tagsForTransaction
            };
        }).ToList();

        return new
        {
            total = items.Count,
            reviewed = items.Count(x => x.isReviewed),
            needsReview = items.Count(x => x.needsReview),
            items
        };
    }

    public static async Task<bool> SetReviewAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, ReviewWrite request, CancellationToken ct)
    {
        var ids = CleanIds(request.TransactionIds);
        if (ids.Count == 0) return true;
        if (ids.Count > 1000) throw new ArgumentException("At most 1000 transactions can be changed at once.");
        var owned = await AccessibleTransactions(db, userId, fullWorthSpaceId, ownerOnly: true)
            .Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
        if (owned.Count != ids.Count) return false;
        foreach (var id in ids) await UpsertReviewAsync(db, fullWorthSpaceId, id, request.IsReviewed, ct);
        return true;
    }

    public static async Task<object?> BulkAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, BulkCategoryAction request, CancellationToken ct)
    {
        var ids = CleanIds(request.TransactionIds);
        if (ids.Count == 0) return new { changed = 0 };
        if (ids.Count > 1000) throw new ArgumentException("At most 1000 transactions can be changed at once.");

        var transactions = await AccessibleTransactions(db, userId, fullWorthSpaceId, ownerOnly: true)
            .Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        if (transactions.Count != ids.Count) return null;

        if (request.UpdateCategory && request.CategoryId.HasValue &&
            !await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId.Value && x.FullWorthSpaceId == fullWorthSpaceId && !x.IsArchived, ct))
            throw new ArgumentException("Category must belong to the active FullWorth Space.");

        var addTags = CleanIds(request.AddTagIds);
        var removeTags = CleanIds(request.RemoveTagIds);
        if (!await TagsBelongToSpaceAsync(db, fullWorthSpaceId, addTags.Concat(removeTags).Distinct().ToList(), ct))
            throw new ArgumentException("Tag must belong to the FullWorth Space.");

        foreach (var transaction in transactions)
        {
            if (request.UpdateCategory)
            {
                transaction.CategoryId = request.CategoryId;
                transaction.CategorizationSource = "manual";
            }
            if (request.IsIgnored.HasValue) transaction.IsIgnored = request.IsIgnored.Value;
            transaction.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        var reviewed = request.IsReviewed ?? (request.UpdateCategory ? true : null);
        foreach (var id in ids)
        {
            if (reviewed.HasValue) await UpsertReviewAsync(db, fullWorthSpaceId, id, reviewed.Value, ct);
            foreach (var tagId in addTags) await AddTransactionTagAsync(db, id, tagId, ct);
            foreach (var tagId in removeTags) await RemoveTransactionTagAsync(db, id, tagId, ct);
        }
        return new { changed = transactions.Count };
    }

    public static async Task<object?> LearnAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, LearnCategoryWrite request, CancellationToken ct)
    {
        var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        if (scope != "one" && scope != "existing" && scope != "future")
            throw new ArgumentException("Scope must be one, existing or future.");
        if (!await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId && x.FullWorthSpaceId == fullWorthSpaceId && !x.IsArchived, ct))
            throw new ArgumentException("Category must belong to the active FullWorth Space.");

        var current = await AccessibleTransactions(db, userId, fullWorthSpaceId, ownerOnly: true)
            .SingleOrDefaultAsync(x => x.Id == request.TransactionId, ct);
        if (current is null) return null;

        var normalized = MerchantNormalization.Normalize(current.NormalizedCounterparty ?? current.Counterparty);
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException("A merchant is required to learn a categorization rule.");
        var direction = current.Amount < 0 ? "expense" : "income";

        var affected = new List<FinanceTransaction> { current };
        if (scope == "existing" || scope == "future")
        {
            var candidatesQuery = AccessibleTransactions(db, userId, fullWorthSpaceId, ownerOnly: true);
            candidatesQuery = direction == "expense"
                ? candidatesQuery.Where(x => x.Amount < 0)
                : candidatesQuery.Where(x => x.Amount > 0);
            var candidates = await candidatesQuery.ToListAsync(ct);
            affected = candidates
                .Where(x => string.Equals(
                    MerchantNormalization.Normalize(x.NormalizedCounterparty ?? x.Counterparty),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        Guid? ruleId = null;
        if (scope == "future")
        {
            var rule = await db.CategorizationRules.SingleOrDefaultAsync(x =>
                x.FullWorthSpaceId == fullWorthSpaceId && x.Target == "transaction" &&
                x.MatchField == "normalized_counterparty" && x.MatchMode == "equals" &&
                x.Pattern == normalized && x.Direction == direction, ct);
            if (rule is null)
            {
                var categoryName = await db.Categories.AsNoTracking()
                    .Where(x => x.Id == request.CategoryId)
                    .Select(x => x.Name)
                    .SingleAsync(ct);
                rule = new CategorizationRule
                {
                    FullWorthSpaceId = fullWorthSpaceId,
                    Name = $"{normalized} → {categoryName}",
                    IsEnabled = true,
                    Priority = 10,
                    Target = "transaction",
                    MatchField = "normalized_counterparty",
                    MatchMode = "equals",
                    Pattern = normalized,
                    Direction = direction,
                    CategoryId = request.CategoryId,
                    StopProcessing = true,
                    MarkAsTransfer = false
                };
                db.CategorizationRules.Add(rule);
            }
            else
            {
                rule.CategoryId = request.CategoryId;
                rule.IsEnabled = true;
                rule.Priority = Math.Min(rule.Priority, 10);
                rule.StopProcessing = true;
                rule.UpdatedAt = DateTimeOffset.UtcNow;
            }
            ruleId = rule.Id;
        }

        foreach (var transaction in affected)
        {
            transaction.CategoryId = request.CategoryId;
            transaction.CategorizationSource = scope == "future" ? "rule" : "manual";
            transaction.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        foreach (var transaction in affected)
            await UpsertReviewAsync(db, fullWorthSpaceId, transaction.Id, reviewed: true, ct);

        return new { changed = affected.Count, ruleId };
    }

    public static async Task<IReadOnlyList<IntelligenceTag>?> ListTagsAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsSpaceMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        return await LoadTagsAsync(db, fullWorthSpaceId, ct);
    }

    public static async Task<IntelligenceTag?> CreateTagAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, TagWrite request, CancellationToken ct)
    {
        if (!await CanCategorizeAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var (name, normalized, color) = ValidateTag(request);
        var id = Guid.NewGuid();
        try
        {
            await ExecuteAsync(db,
                "INSERT INTO \"FinanceTags\" (\"Id\", \"FullWorthSpaceId\", \"Name\", \"NormalizedName\", \"Color\", \"CreatedAt\", \"UpdatedAt\") VALUES (@id, @space, @name, @normalized, @color, @now, @now)",
                ct, ("id", id), ("space", fullWorthSpaceId), ("name", name), ("normalized", normalized), ("color", color), ("now", DateTimeOffset.UtcNow));
        }
        catch (DbException exception) when (IsUniqueViolation(exception))
        {
            throw new ArgumentException("A tag with this name already exists.");
        }
        return new(id, name, color);
    }

    public static async Task<IntelligenceTag?> UpdateTagAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, Guid tagId, TagWrite request, CancellationToken ct)
    {
        if (!await CanCategorizeAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var (name, normalized, color) = ValidateTag(request);
        try
        {
            var changed = await ExecuteAsync(db,
                "UPDATE \"FinanceTags\" SET \"Name\"=@name, \"NormalizedName\"=@normalized, \"Color\"=@color, \"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",
                ct, ("name", name), ("normalized", normalized), ("color", color), ("now", DateTimeOffset.UtcNow), ("id", tagId), ("space", fullWorthSpaceId));
            return changed == 0 ? null : new(tagId, name, color);
        }
        catch (DbException exception) when (IsUniqueViolation(exception))
        {
            throw new ArgumentException("A tag with this name already exists.");
        }
    }

    public static async Task<bool> DeleteTagAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, Guid tagId, CancellationToken ct)
    {
        if (!await CanCategorizeAsync(db, userId, fullWorthSpaceId, ct)) return false;
        return await ExecuteAsync(db, "DELETE FROM \"FinanceTags\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space", ct, ("id", tagId), ("space", fullWorthSpaceId)) > 0;
    }

    public static async Task<bool> ReplaceTagsAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, Guid transactionId, TagAssignmentWrite request, CancellationToken ct)
    {
        if (!await AccessibleTransactions(db, userId, fullWorthSpaceId, ownerOnly: true).AnyAsync(x => x.Id == transactionId, ct)) return false;
        var ids = CleanIds(request.TagIds);
        if (!await TagsBelongToSpaceAsync(db, fullWorthSpaceId, ids, ct))
            throw new ArgumentException("Tag must belong to the FullWorth Space.");
        await ExecuteAsync(db, "DELETE FROM \"TransactionTags\" WHERE \"TransactionId\"=@transaction", ct, ("transaction", transactionId));
        foreach (var id in ids) await AddTransactionTagAsync(db, transactionId, id, ct);
        return true;
    }

    public static async Task<IReadOnlyList<CategoryAppearanceView>?> ListAppearancesAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsSpaceMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        return (await LoadAppearancesAsync(db, fullWorthSpaceId, ct))
            .Select(x => new CategoryAppearanceView(x.CategoryId, x.Color))
            .ToList();
    }

    public static async Task<bool> SetAppearanceAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, Guid categoryId, CategoryAppearanceWrite request, CancellationToken ct)
    {
        if (!await CanCategorizeAsync(db, userId, fullWorthSpaceId, ct)) return false;
        if (!await db.Categories.AsNoTracking().AnyAsync(x => x.Id == categoryId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return false;
        var color = ValidateColor(request.Color);
        await ExecuteAsync(db,
            "INSERT INTO \"CategoryAppearances\" (\"CategoryId\", \"FullWorthSpaceId\", \"Color\", \"UpdatedAt\") VALUES (@category, @space, @color, @now) ON CONFLICT (\"CategoryId\") DO UPDATE SET \"Color\"=EXCLUDED.\"Color\", \"UpdatedAt\"=EXCLUDED.\"UpdatedAt\"",
            ct, ("category", categoryId), ("space", fullWorthSpaceId), ("color", color), ("now", DateTimeOffset.UtcNow));
        return true;
    }

    private static IQueryable<FinanceTransaction> AccessibleTransactions(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, bool ownerOnly) =>
        db.Transactions.Where(transaction => db.Accounts.Any(account =>
            account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId && (!ownerOnly || owner.OwnershipType == AccountOwnershipTypes.Owner))));

    private static Task<bool> IsSpaceMemberAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);

    private static Task<bool> CanCategorizeAsync(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        FullWorth.Backend.Modules.Parity.PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(
            db, userId, fullWorthSpaceId, "transactions.categorize", ct);

    private static List<Guid> CleanIds(IEnumerable<Guid>? ids) => ids?.Where(x => x != Guid.Empty).Distinct().ToList() ?? [];

    private static (string Name, string Normalized, string? Color) ValidateTag(TagWrite request)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("Tag name must contain 1 to 80 characters.");
        var normalized = MerchantNormalization.Normalize(name);
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException("Tag name must contain a letter or number.");
        return (name, normalized, ValidateColor(request.Color));
    }

    public static string? ValidateColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var color = value.Trim().ToUpperInvariant();
        if (color.Length is not (7 or 9) || color[0] != '#' || color[1..].Any(x => !Uri.IsHexDigit(x)))
            throw new ArgumentException("Color must be #RRGGBB or #RRGGBBAA.");
        return color;
    }

    private static bool IsUniqueViolation(DbException exception) =>
        exception.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<Guid, ReviewState>> LoadReviewsAsync(FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ReviewState>();
        await using var command = await CommandAsync(db,
            "SELECT \"TransactionId\", \"IsReviewed\" FROM \"TransactionReviewStates\" WHERE \"FullWorthSpaceId\"=@space",
            ct, ("space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetGuid(0)] = new(reader.GetBoolean(1));
        return result;
    }

    private static async Task<IReadOnlyList<IntelligenceTag>> LoadTagsAsync(FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var result = new List<IntelligenceTag>();
        await using var command = await CommandAsync(db,
            "SELECT \"Id\", \"Name\", \"Color\" FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"Name\"",
            ct, ("space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return result;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<IntelligenceTag>>> LoadTransactionTagsAsync(FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var buffer = new Dictionary<Guid, List<IntelligenceTag>>();
        await using var command = await CommandAsync(db,
            "SELECT tt.\"TransactionId\", t.\"Id\", t.\"Name\", t.\"Color\" FROM \"TransactionTags\" tt JOIN \"FinanceTags\" t ON t.\"Id\"=tt.\"TagId\" WHERE t.\"FullWorthSpaceId\"=@space ORDER BY t.\"Name\"",
            ct, ("space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var transactionId = reader.GetGuid(0);
            if (!buffer.TryGetValue(transactionId, out var items)) buffer[transactionId] = items = [];
            items.Add(new(reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return buffer.ToDictionary(x => x.Key, x => (IReadOnlyList<IntelligenceTag>)x.Value);
    }

    private static async Task<IReadOnlyList<Appearance>> LoadAppearancesAsync(FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var result = new List<Appearance>();
        await using var command = await CommandAsync(db,
            "SELECT \"CategoryId\", \"Color\" FROM \"CategoryAppearances\" WHERE \"FullWorthSpaceId\"=@space",
            ct, ("space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return result;
    }

    private static async Task<bool> TagsBelongToSpaceAsync(FullWorthDbContext db, Guid fullWorthSpaceId, IReadOnlyList<Guid> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0) return true;
        var known = (await LoadTagsAsync(db, fullWorthSpaceId, ct)).Select(x => x.Id).ToHashSet();
        return tagIds.All(known.Contains);
    }

    private static Task<int> UpsertReviewAsync(FullWorthDbContext db, Guid fullWorthSpaceId, Guid transactionId, bool reviewed, CancellationToken ct) =>
        ExecuteAsync(db,
            "INSERT INTO \"TransactionReviewStates\" (\"TransactionId\", \"FullWorthSpaceId\", \"IsReviewed\", \"UpdatedAt\") VALUES (@transaction, @space, @reviewed, @now) ON CONFLICT (\"TransactionId\") DO UPDATE SET \"IsReviewed\"=EXCLUDED.\"IsReviewed\", \"UpdatedAt\"=EXCLUDED.\"UpdatedAt\"",
            ct, ("transaction", transactionId), ("space", fullWorthSpaceId), ("reviewed", reviewed), ("now", DateTimeOffset.UtcNow));

    private static Task<int> AddTransactionTagAsync(FullWorthDbContext db, Guid transactionId, Guid tagId, CancellationToken ct) =>
        ExecuteAsync(db,
            "INSERT INTO \"TransactionTags\" (\"TransactionId\", \"TagId\", \"CreatedAt\") VALUES (@transaction, @tag, @now) ON CONFLICT (\"TransactionId\", \"TagId\") DO NOTHING",
            ct, ("transaction", transactionId), ("tag", tagId), ("now", DateTimeOffset.UtcNow));

    private static Task<int> RemoveTransactionTagAsync(FullWorthDbContext db, Guid transactionId, Guid tagId, CancellationToken ct) =>
        ExecuteAsync(db,
            "DELETE FROM \"TransactionTags\" WHERE \"TransactionId\"=@transaction AND \"TagId\"=@tag",
            ct, ("transaction", transactionId), ("tag", tagId));

    private static async Task<int> ExecuteAsync(FullWorthDbContext db, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = await CommandAsync(db, sql, ct, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DbCommand> CommandAsync(FullWorthDbContext db, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}

public static class CategoryIntelligenceEndpoints
{
    public static IEndpointRouteBuilder MapCategoryIntelligenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/category-intelligence").WithTags("Category Intelligence");

        group.MapGet("/overview", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var result = await CategoryIntelligenceStore.OverviewAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/review", async (Guid fullWorthSpaceId, ReviewWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try { return await CategoryIntelligenceStore.SetReviewAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, request, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/bulk", async (Guid fullWorthSpaceId, BulkCategoryAction request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var result = await CategoryIntelligenceStore.BulkAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/learn", async (Guid fullWorthSpaceId, LearnCategoryWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var result = await CategoryIntelligenceStore.LearnAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapGet("/tags", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var result = await CategoryIntelligenceStore.ListTagsAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/tags", async (Guid fullWorthSpaceId, TagWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var result = await CategoryIntelligenceStore.CreateTagAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return result is null ? Results.NotFound() : Results.Created($"/api/category-intelligence/tags/{result.Id}", result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPut("/tags/{tagId:guid}", async (Guid tagId, Guid fullWorthSpaceId, TagWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var result = await CategoryIntelligenceStore.UpdateTagAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, tagId, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapDelete("/tags/{tagId:guid}", async (Guid tagId, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
            await CategoryIntelligenceStore.DeleteTagAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, tagId, ct) ? Results.NoContent() : Results.NotFound());

        group.MapPut("/transactions/{transactionId:guid}/tags", async (Guid transactionId, Guid fullWorthSpaceId, TagAssignmentWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try { return await CategoryIntelligenceStore.ReplaceTagsAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, transactionId, request, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapGet("/category-appearances", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var result = await CategoryIntelligenceStore.ListAppearancesAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPut("/category-appearances/{categoryId:guid}", async (Guid categoryId, Guid fullWorthSpaceId, CategoryAppearanceWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try { return await CategoryIntelligenceStore.SetAppearanceAsync(db, currentUser.RequireUserId(), fullWorthSpaceId, categoryId, request, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }
}
