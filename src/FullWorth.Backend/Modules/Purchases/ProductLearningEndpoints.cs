using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record ProductCategoryLearningAccept(
    string Text, Guid CategoryId, Guid? ProductIdentityId = null, string? CanonicalName = null);

/// <summary>
/// Was das System aus den Kategorien lernt, die ein Mensch selbst vergeben hat.
///
/// Hier steht, wann ein Muster ein Vorschlag wird, und diese Schwellen sind die Aussage der Route:
/// mindestens drei Mal dieselbe Kategorie fuer denselben Namen, und ein eindeutiger Sieger. Bei
/// einem Gleichstand wird nichts vorgeschlagen - ein falscher Vorschlag kostet mehr Vertrauen, als
/// ein fehlender an Bequemlichkeit bringt.
/// </summary>
public static class ProductLearningEndpoints
{
    /// <summary>Unter drei Beobachtungen ist es Zufall, nicht Gewohnheit.</summary>
    private const int MinimumObservations = 3;

    /// <summary>Mehr Vorschlaege sieht sich niemand an.</summary>
    private const int MaxSuggestions = 200;

    public static IEndpointRouteBuilder MapProductLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/product-learning").WithTags("Purchases");
        group.MapGet("/category-suggestions", Suggestions);
        group.MapPost("/category-suggestions/accept", AcceptSuggestion);
        group.MapGet("/products/{productId:guid}/aliases", Aliases);
        return app;
    }

    private static async Task<IResult> Suggestions(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ProductLearningStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = await store.ManuallyCategorizedItemsAsync(userId, fullWorthSpaceId, ct);
        var candidates = rows
            .Select(row => new { row.Name, Normalized = Normalize(row.Name), row.CategoryId })
            .Where(row => row.Normalized.Length >= 2)
            .GroupBy(row => row.Normalized)
            .Select(group =>
            {
                var byCategory = group.GroupBy(row => row.CategoryId)
                    .Select(category => new { CategoryId = category.Key, Count = category.Count() })
                    .OrderByDescending(category => category.Count).ThenBy(category => category.CategoryId)
                    .ToArray();
                var best = byCategory[0];
                return new
                {
                    Normalized = group.Key,
                    DisplayName = group.GroupBy(row => row.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(names => names.Count()).ThenBy(names => names.Key).First().Key,
                    best.CategoryId,
                    best.Count,
                    Total = group.Count(),
                    UniqueWinner = byCategory.Length == 1 || best.Count > byCategory[1].Count
                };
            })
            .Where(candidate => candidate.UniqueWinner && candidate.Count >= MinimumObservations)
            .OrderByDescending(candidate => candidate.Count).ThenBy(candidate => candidate.DisplayName)
            .Take(MaxSuggestions)
            .ToArray();

        if (candidates.Length == 0) return Results.Ok(Array.Empty<object>());

        var categoryNames = await store.CategoryNamesAsync(
            fullWorthSpaceId, candidates.Select(candidate => candidate.CategoryId).Distinct().ToArray(), ct);
        var byAlias = (await store.AliasesForAsync(
                fullWorthSpaceId,
                candidates.Select(candidate => candidate.Normalized).ToHashSet(StringComparer.Ordinal), ct))
            .GroupBy(alias => alias.NormalizedAlias)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return Results.Ok(candidates
            // Was schon so hinterlegt ist, ist kein Vorschlag mehr.
            .Where(candidate => !byAlias.TryGetValue(candidate.Normalized, out var known)
                             || known.DefaultCategoryId != candidate.CategoryId)
            .Select(candidate =>
            {
                byAlias.TryGetValue(candidate.Normalized, out var known);
                return new
                {
                    text = candidate.DisplayName,
                    normalizedText = candidate.Normalized,
                    categoryId = candidate.CategoryId,
                    category = categoryNames.GetValueOrDefault(candidate.CategoryId, string.Empty),
                    count = candidate.Count,
                    totalOccurrences = candidate.Total,
                    productIdentityId = known?.ProductId,
                    productName = known?.ProductName,
                    currentDefaultCategoryId = known?.DefaultCategoryId
                };
            }));
    }

    private static async Task<IResult> AcceptSuggestion(
        Guid fullWorthSpaceId, ProductCategoryLearningAccept request, CurrentUserContext currentUser,
        SpaceAccess space, ProductLearningStore store, IntelligenceFeedbackRecorder intelligenceFeedback,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Text))
            return Results.BadRequest(new { error = "Product text is required." });

        var targetCategory = await store.FindTargetCategoryAsync(fullWorthSpaceId, request.CategoryId, ct);
        if (targetCategory is null) return Results.BadRequest(new { error = "Category is invalid." });

        var normalized = Normalize(request.Text);
        if (normalized.Length < 2) return Results.BadRequest(new { error = "Product text is too short." });

        var existing = request.ProductIdentityId.HasValue
            ? await store.FindProductAsync(fullWorthSpaceId, request.ProductIdentityId.Value, ct)
            : await store.FindProductByAliasAsync(fullWorthSpaceId, normalized, ct);
        if (request.ProductIdentityId.HasValue && existing is null)
            return Results.BadRequest(new { error = "Product is invalid." });

        var oldCategoryId = existing?.DefaultCategoryId;
        var product = await store.LearnAsync(
            userId, fullWorthSpaceId, existing, normalized, request.Text, request.CanonicalName,
            request.CategoryId, ct);

        // Nur eine Systemkategorie ist ueberhaupt vergleichbar; ein eigener Name des Haushalts sagt
        // ausserhalb nichts. Und nur die GTIN identifiziert ein Produkt oeffentlich.
        string? publicProductKey = null;
        if (targetCategory.IsSystem)
            foreach (var barcode in await store.BarcodesAsync(product.Id, ct))
                if (GtinKey.TryCreateGtinSubjectKey(barcode, out var key))
                {
                    publicProductKey = key;
                    break;
                }

        await intelligenceFeedback.RecordProductCategoryAsync(
            fullWorthSpaceId, userId, product.Id, normalized, oldCategoryId, request.CategoryId, ct,
            publicProductKey, targetCategory.IsSystem ? targetCategory.Key : null);

        return Results.Ok(new
        {
            productIdentityId = product.Id,
            normalizedText = normalized,
            categoryId = request.CategoryId
        });
    }

    private static async Task<IResult> Aliases(
        Guid productId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ProductLearningStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await store.ProductExistsAsync(fullWorthSpaceId, productId, ct)) return Results.NotFound();

        var rows = await store.ListAliasesAsync(productId, ct);
        return Results.Ok(rows.Select(alias => new
        {
            id = alias.Id,
            text = alias.Alias,
            normalizedText = alias.NormalizedAlias,
            confidence = (decimal?)1m,
            source = alias.AliasType,
            createdAt = alias.CreatedAt
        }));
    }

    /// <summary>
    /// Zwei Namen sind derselbe, wenn nur Buchstaben und Ziffern zaehlen: "Bio-Milch 1,5%" und
    /// "BIO MILCH 1.5 %" sollen zusammen gelernt werden.
    /// </summary>
    private static string Normalize(string value) =>
        new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
