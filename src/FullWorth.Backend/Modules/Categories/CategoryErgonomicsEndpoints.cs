using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Categories;

public sealed record CategoryMergeWrite(Guid TargetCategoryId, bool ArchiveSource = true);
public sealed record CategoryOrderItem(Guid CategoryId, Guid? ParentId, int SortOrder);
public sealed record CategoryOrderWrite(IReadOnlyList<CategoryOrderItem>? Items);

/// <summary>Kategorien zusammenfuehren, umsortieren, ihre Verweise zaehlen. Kam aus Parity.</summary>
public static class CategoryErgonomicsEndpoints
{
    private const int MaxItems = 1000;

    public static IEndpointRouteBuilder MapCategoryErgonomicsEndpoints(this IEndpointRouteBuilder app)
    {
        var categories = app.MapGroup("/api/category-ergonomics").WithTags("Categories");
        categories.MapGet("/{categoryId:guid}/references", CategoryReferences);
        categories.MapPost("/{categoryId:guid}/merge", MergeCategory);
        categories.MapPost("/reorder", ReorderCategories);
        return app;
    }

    private static async Task<IResult> CategoryReferences(
        Guid categoryId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        CategoryErgonomicsStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await store.ExistsAsync(fullWorthSpaceId, categoryId, ct)) return Results.NotFound();

        var counts = await store.ReferenceCountsAsync(categoryId, ct);
        return Results.Ok(new
        {
            transactions = counts.Transactions,
            allocations = counts.Allocations,
            rules = counts.Rules,
            budgets = counts.Budgets,
            contracts = counts.Contracts,
            purchaseItems = counts.PurchaseItems,
            productDefaults = counts.ProductDefaults,
            refunds = counts.Refunds
        });
    }

    private static async Task<IResult> MergeCategory(
        Guid categoryId, Guid fullWorthSpaceId, CategoryMergeWrite request, CurrentUserContext currentUser,
        SpaceAccess space, CategoryErgonomicsStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (categoryId == request.TargetCategoryId)
            return Results.BadRequest(new { error = "Source and target must differ." });

        var byId = (await store.ListAsync(fullWorthSpaceId, ct)).ToDictionary(category => category.Id);
        if (!byId.TryGetValue(categoryId, out var source) ||
            !byId.TryGetValue(request.TargetCategoryId, out var target)) return Results.NotFound();

        // Merging a parent into one of its descendants and then reparenting its children to that
        // descendant can create A -> B -> ... -> A. Reject before any financial references move.
        Guid? cursor = target.ParentId;
        while (cursor.HasValue)
        {
            if (cursor.Value == source.Id)
                return Results.BadRequest(new { error = "Target category is a descendant of the source. Reparent it first." });
            cursor = byId.GetValueOrDefault(cursor.Value)?.ParentId;
        }

        await store.MergeAsync(userId, fullWorthSpaceId, source, request.TargetCategoryId, request.ArchiveSource, ct);
        return Results.Ok(new
        {
            sourceCategoryId = categoryId,
            targetCategoryId = request.TargetCategoryId,
            archived = source.IsArchived
        });
    }

    private static async Task<IResult> ReorderCategories(
        Guid fullWorthSpaceId, CategoryOrderWrite request, CurrentUserContext currentUser,
        SpaceAccess space, CategoryErgonomicsStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var items = (request.Items ?? []).DistinctBy(item => item.CategoryId).ToArray();
        if (items.Length == 0 || items.Length > MaxItems)
            return Results.BadRequest(new { error = "Invalid category order request." });

        var all = await store.ListAsync(fullWorthSpaceId, ct);
        var byId = all.ToDictionary(category => category.Id);
        if (items.Any(item => !byId.ContainsKey(item.CategoryId) ||
                              item.ParentId.HasValue && !byId.ContainsKey(item.ParentId.Value)))
            return Results.BadRequest(new { error = "Category does not belong to this FullWorth Space." });

        var proposedParents = all.ToDictionary(category => category.Id, category => category.ParentId);
        foreach (var item in items) proposedParents[item.CategoryId] = item.ParentId;
        if (CategoryOrderService.HasCycle(proposedParents))
            return Results.BadRequest(new { error = "Category hierarchy would contain a cycle." });

        await store.ReorderAsync(userId, fullWorthSpaceId, byId, items, ct);
        return Results.NoContent();
    }
}
