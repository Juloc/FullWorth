using FullWorth.Backend.Modules.Merchants;

namespace FullWorth.Backend.Modules.Categories;

/// <summary>Eine Buchung in der Kategorie-Ansicht, mit der Begruendung ihrer Einordnung.</summary>
public sealed record CategoryOverviewItem(
    Guid Id, Guid? CategoryId, string? CategorizationSource, bool IsReviewed, bool NeedsReview, decimal Confidence,
    string ReasonCode, string? Detail, bool LearningSuggested, string? CategoryColor,
    IReadOnlyList<IntelligenceTag> Tags);

/// <summary>Die Kategorie-Ansicht: wie viele Buchungen geprueft sind und welche noch offen.</summary>
public sealed record CategoryOverviewView(
    int Total, int Reviewed, int NeedsReview, IReadOnlyList<CategoryOverviewItem> Items);

/// <summary>Wie viele Buchungen eine Massenaenderung getroffen hat.</summary>
public sealed record BulkResult(int Changed);

/// <summary>Wie viele Buchungen umkategorisiert wurden, und welche Regel dabei entstand.</summary>
public sealed record LearnResult(int Changed, Guid? RuleId);

/// <summary>
/// Die Ablaeufe hinter der Kategorie-Ansicht: pruefen, in Menge aendern und aus einer Korrektur
/// lernen.
///
/// Drei Entscheidungen stecken hier drin, die man den Zahlen nicht ansieht:
///
/// Der Pruefzustand hat einen Vorgabewert. Wer eine Buchung von Hand eingeordnet hat, hat sie damit
/// geprueft - ebenso eine Einordnung, die aus einem fremden System mitkam. Nur was die Regeln oder
/// der Katalog geraten haben, wartet auf einen Menschen. Ein ausdruecklicher Eintrag schlaegt den
/// Vorgabewert immer.
///
/// <c>learningSuggested</c> entsteht aus drei gleichen Entscheidungen zum selben Haendler in derselben
/// Richtung. Weniger waere Zufall; die Richtung gehoert dazu, weil eine Rueckerstattung desselben
/// Haendlers nicht dieselbe Kategorie braucht wie der Kauf.
///
/// Beim Lernen unterscheiden sich die drei Reichweiten in dem, was sie hinterlassen: "one" und
/// "existing" schreiben nur Buchungen um und gelten als Handarbeit, "future" legt zusaetzlich eine
/// Regel an - und erst dann tragen die Buchungen die Herkunft "rule".
/// </summary>
public sealed class CategoryIntelligenceService(CategoryIntelligenceStore store)
{
    /// <summary>So viele Buchungen zeigt die Ansicht hoechstens - darueber hinaus hilft die Suche.</summary>
    private const int OverviewLimit = 5000;

    /// <summary>So viele Buchungen darf eine Massenaenderung treffen.</summary>
    private const int BulkLimit = 1000;

    public async Task<CategoryOverviewView?> OverviewAsync(Guid userId, Guid space, CancellationToken ct)
    {
        if (!await store.IsMemberAsync(userId, space, ct)) return null;

        var transactions = await store.RecentVisibleAsync(userId, space, OverviewLimit, ct);
        var rules = await store.ActiveRulesAsync(space, ct);
        var reviews = await store.ReviewStatesAsync(space, ct);
        var tagsByTransaction = await store.TagsByTransactionAsync(space, ct);
        var colorByCategory = (await store.AppearancesAsync(space, ct))
            .ToDictionary(appearance => appearance.CategoryId, appearance => appearance.Color);

        var repeatedManual = transactions
            .Where(transaction => transaction.CategoryId.HasValue && transaction.CategorizationSource == "manual")
            .Select(transaction => new
            {
                Merchant = MerchantNormalization.Normalize(
                    transaction.NormalizedCounterparty ?? transaction.Counterparty),
                CategoryId = transaction.CategoryId!.Value,
                Direction = Math.Sign(transaction.Amount)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Merchant))
            .GroupBy(entry => (entry.Merchant!, entry.CategoryId, entry.Direction))
            .Where(group => group.Count() >= 3)
            .Select(group => group.Key)
            .ToHashSet();

        var items = transactions.Select(transaction =>
        {
            var explanation = CategoryIntelligenceExplanation.Explain(transaction, rules);
            var source = transaction.CategorizationSource?.Trim().ToLowerInvariant() ?? "none";
            var fromAnotherSystem = transaction.CategoryId.HasValue
                && source is not ("none" or "rule" or "catalog" or "manual");
            var reviewed = reviews.TryGetValue(transaction.Id, out var state)
                ? state.IsReviewed
                : source == "manual" || fromAnotherSystem;

            var merchant = MerchantNormalization.Normalize(
                transaction.NormalizedCounterparty ?? transaction.Counterparty);
            var learningSuggested = transaction.CategoryId.HasValue && !string.IsNullOrWhiteSpace(merchant)
                && repeatedManual.Contains((merchant!, transaction.CategoryId.Value, Math.Sign(transaction.Amount)));

            return new CategoryOverviewItem(
                transaction.Id, transaction.CategoryId, transaction.CategorizationSource, reviewed, !reviewed,
                explanation.Confidence, explanation.ReasonCode, explanation.Detail, learningSuggested,
                transaction.CategoryId.HasValue && colorByCategory.TryGetValue(transaction.CategoryId.Value, out var color)
                    ? color
                    : null,
                tagsByTransaction.TryGetValue(transaction.Id, out var tags) ? tags : []);
        }).ToList();

        return new CategoryOverviewView(
            items.Count, items.Count(item => item.IsReviewed), items.Count(item => item.NeedsReview), items);
    }

    public async Task<bool> SetReviewAsync(Guid userId, Guid space, ReviewWrite request, CancellationToken ct)
    {
        var ids = CleanIds(request.TransactionIds);
        if (ids.Count == 0) return true;
        if (ids.Count > BulkLimit) throw new ArgumentException($"At most {BulkLimit} transactions can be changed at once.");

        // Entweder alle oder keine: eine halb ausgefuehrte Massenaenderung waere fuer den Benutzer
        // nicht davon zu unterscheiden, dass sie ganz gelaufen ist.
        var writable = await store.WritableAsync(userId, space, ids, ct);
        if (writable.Count != ids.Count) return false;

        foreach (var id in ids) await store.SetReviewedAsync(space, id, request.IsReviewed, ct);
        return true;
    }

    public async Task<BulkResult?> BulkAsync(
        Guid userId, Guid space, BulkCategoryAction request, CancellationToken ct)
    {
        var ids = CleanIds(request.TransactionIds);
        if (ids.Count == 0) return new BulkResult(0);
        if (ids.Count > BulkLimit) throw new ArgumentException($"At most {BulkLimit} transactions can be changed at once.");

        var transactions = await store.WritableAsync(userId, space, ids, ct);
        if (transactions.Count != ids.Count) return null;

        if (request.UpdateCategory && request.CategoryId.HasValue
            && !await store.CategoryUsableAsync(space, request.CategoryId.Value, ct))
            throw new ArgumentException("Category must belong to the active FullWorth Space.");

        var addTags = CleanIds(request.AddTagIds);
        var removeTags = CleanIds(request.RemoveTagIds);
        await EnsureTagsBelongToSpaceAsync(space, addTags.Concat(removeTags).Distinct().ToList(), ct);

        await store.ApplyAsync(transactions, request.UpdateCategory, request.CategoryId, "manual",
            request.IsIgnored, ct);

        // Wer eine Kategorie setzt, hat damit geprueft - ausser er sagt ausdruecklich etwas anderes.
        var reviewed = request.IsReviewed ?? (request.UpdateCategory ? true : null);
        foreach (var id in ids)
        {
            if (reviewed.HasValue) await store.SetReviewedAsync(space, id, reviewed.Value, ct);
            foreach (var tagId in addTags) await store.AddTagAsync(id, tagId, ct);
            foreach (var tagId in removeTags) await store.RemoveTagAsync(id, tagId, ct);
        }
        return new BulkResult(transactions.Count);
    }

    public async Task<LearnResult?> LearnAsync(
        Guid userId, Guid space, LearnCategoryWrite request, CancellationToken ct)
    {
        var scope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        if (scope is not ("one" or "existing" or "future"))
            throw new ArgumentException("Scope must be one, existing or future.");
        if (!await store.CategoryUsableAsync(space, request.CategoryId, ct))
            throw new ArgumentException("Category must belong to the active FullWorth Space.");

        var current = await store.FindWritableAsync(userId, space, request.TransactionId, ct);
        if (current is null) return null;

        var merchant = MerchantNormalization.Normalize(current.NormalizedCounterparty ?? current.Counterparty);
        if (string.IsNullOrWhiteSpace(merchant))
            throw new ArgumentException("A merchant is required to learn a categorization rule.");
        var direction = current.Amount < 0 ? "expense" : "income";

        var affected = new List<Transactions.FinanceTransaction> { current };
        if (scope is "existing" or "future")
            affected = (await store.WritableByDirectionAsync(userId, space, direction == "expense", ct))
                .Where(transaction => string.Equals(
                    MerchantNormalization.Normalize(transaction.NormalizedCounterparty ?? transaction.Counterparty),
                    merchant, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Guid? ruleId = null;
        if (scope == "future")
            ruleId = await store.SaveMerchantRuleAsync(space, merchant!, direction, request.CategoryId,
                $"{merchant} → {await store.CategoryNameAsync(request.CategoryId, ct)}", ct);

        await store.ApplyAsync(affected, updateCategory: true, request.CategoryId,
            scope == "future" ? "rule" : "manual", isIgnored: null, ct);
        foreach (var transaction in affected) await store.SetReviewedAsync(space, transaction.Id, true, ct);

        return new LearnResult(affected.Count, ruleId);
    }

    public async Task<IReadOnlyList<IntelligenceTag>?> ListTagsAsync(Guid userId, Guid space, CancellationToken ct) =>
        await store.IsMemberAsync(userId, space, ct) ? await store.TagsAsync(space, ct) : null;

    public async Task<IntelligenceTag?> CreateTagAsync(
        Guid userId, Guid space, TagWrite request, CancellationToken ct)
    {
        if (!await store.CanCategorizeAsync(userId, space, ct)) return null;
        var (name, normalized, color) = ValidateTag(request);

        var id = Guid.NewGuid();
        try
        {
            await store.InsertTagAsync(space, id, name, normalized, color, ct);
        }
        catch (Exception exception) when (IsUniqueViolation(exception))
        {
            throw new ArgumentException("A tag with this name already exists.");
        }
        return new IntelligenceTag(id, name, color);
    }

    public async Task<IntelligenceTag?> UpdateTagAsync(
        Guid userId, Guid space, Guid tagId, TagWrite request, CancellationToken ct)
    {
        if (!await store.CanCategorizeAsync(userId, space, ct)) return null;
        var (name, normalized, color) = ValidateTag(request);

        try
        {
            return await store.UpdateTagAsync(space, tagId, name, normalized, color, ct)
                ? new IntelligenceTag(tagId, name, color)
                : null;
        }
        catch (Exception exception) when (IsUniqueViolation(exception))
        {
            throw new ArgumentException("A tag with this name already exists.");
        }
    }

    public async Task<bool> DeleteTagAsync(Guid userId, Guid space, Guid tagId, CancellationToken ct) =>
        await store.CanCategorizeAsync(userId, space, ct) && await store.DeleteTagAsync(space, tagId, ct);

    public async Task<bool> ReplaceTagsAsync(
        Guid userId, Guid space, Guid transactionId, TagAssignmentWrite request, CancellationToken ct)
    {
        if (!await store.IsWritableAsync(userId, space, transactionId, ct)) return false;
        var ids = CleanIds(request.TagIds);
        await EnsureTagsBelongToSpaceAsync(space, ids, ct);

        await store.ClearTagsAsync(transactionId, ct);
        foreach (var id in ids) await store.AddTagAsync(transactionId, id, ct);
        return true;
    }

    public async Task<IReadOnlyList<CategoryAppearanceView>?> ListAppearancesAsync(
        Guid userId, Guid space, CancellationToken ct) =>
        await store.IsMemberAsync(userId, space, ct)
            ? (await store.AppearancesAsync(space, ct))
                .Select(appearance => new CategoryAppearanceView(appearance.CategoryId, appearance.Color)).ToList()
            : null;

    public async Task<bool> SetAppearanceAsync(
        Guid userId, Guid space, Guid categoryId, CategoryAppearanceWrite request, CancellationToken ct)
    {
        if (!await store.CanCategorizeAsync(userId, space, ct)) return false;
        if (!await store.CategoryExistsAsync(space, categoryId, ct)) return false;

        await store.SetAppearanceAsync(space, categoryId, ValidateColor(request.Color), ct);
        return true;
    }

    private async Task EnsureTagsBelongToSpaceAsync(Guid space, IReadOnlyList<Guid> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0) return;
        var known = (await store.TagsAsync(space, ct)).Select(tag => tag.Id).ToHashSet();
        if (!tagIds.All(known.Contains)) throw new ArgumentException("Tag must belong to the FullWorth Space.");
    }

    private static List<Guid> CleanIds(IEnumerable<Guid>? ids) =>
        ids?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];

    private static (string Name, string Normalized, string? Color) ValidateTag(TagWrite request)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("Tag name must contain 1 to 80 characters.");
        var normalized = MerchantNormalization.Normalize(name);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Tag name must contain a letter or number.");
        return (name, normalized, ValidateColor(request.Color));
    }

    public static string? ValidateColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var color = value.Trim().ToUpperInvariant();
        if (color.Length is not (7 or 9) || color[0] != '#' || color[1..].Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Color must be #RRGGBB or #RRGGBBAA.");
        return color;
    }

    private static bool IsUniqueViolation(Exception exception) =>
        exception is System.Data.Common.DbException &&
        (exception.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
}
