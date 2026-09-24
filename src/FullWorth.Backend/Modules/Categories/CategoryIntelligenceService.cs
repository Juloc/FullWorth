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
public sealed class CategoryIntelligenceService(
    CategoryIntelligenceStore store,
    Intelligence.IntelligenceFeedbackRecorder feedback)
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

        // "Kuenftig so" ist eine bestaetigte Haendler-zu-Kategorie-Zuordnung - dieselbe Erkenntnis wie
        // ein angenommener KI-Vorschlag, nur von Hand. Sie ging bisher nur in die lokale Regel; die
        // Cloud erfuhr davon nichts, obwohl "REWE ist Lebensmittel" fuer jeden gilt. Bei "diese eine"
        // oder "alle bisherigen" wird bewusst nichts gemeldet: dort sagt der Benutzer etwas ueber
        // seine Buchungen, nicht ueber den Haendler.
        if (scope == "future" && await store.CategoryFactsAsync(request.CategoryId, ct) is { } category)
        {
            await feedback.RecordMerchantMappingConfirmedAsync(
                space, userId, merchant, direction, category.Key, category.Name,
                "merchant_rule_confirmed", ct, categoryIsCustom: !category.IsSystem);
        }

        return new LearnResult(affected.Count, ruleId);
    }

    // Hier standen ListTagsAsync, CreateTagAsync, UpdateTagAsync, DeleteTagAsync und
    // ReplaceTagsAsync. Sie bedienten die fuenf Etikett-Routen, die seit #124 als /api/collections
    // laufen - mit Symbol, Zeitraum, Status und Massenzuordnung, die es hier nie gab.
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

    private static List<Guid> CleanIds(IEnumerable<Guid>? ids) =>
        ids?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];

    public static string? ValidateColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var color = value.Trim().ToUpperInvariant();
        if (color.Length is not (7 or 9) || color[0] != '#' || color[1..].Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Color must be #RRGGBB or #RRGGBBAA.");
        return color;
    }

}
