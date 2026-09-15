using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Categories;

public sealed record CategoryMergeApplyWrite(Guid TargetCategoryId, bool DeleteSource = false);

/// <summary>
/// Zwei Kategorien zusammenfuehren - erst zeigen, was daran haengt, dann umhaengen.
///
/// Die Vorschau ist keine Bequemlichkeit: sie nennt die Zahl der Buchungen, Regeln, Budgets und
/// Vertraege, die sich gleich aendern, und sie sagt vorher, ob es ueberhaupt geht. Wer eine Kategorie
/// zusammenfuehrt, aendert damit rueckwirkend jede Auswertung.
/// </summary>
public static class CategoryMergeEndpoints
{
    public static IEndpointRouteBuilder MapCategoryMergeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/category-merge").WithTags("Categories");
        group.MapGet("/{sourceCategoryId:guid}/preview", Preview);
        group.MapPost("/{sourceCategoryId:guid}", Apply);
        return app;
    }

    private static async Task<IResult> Preview(
        Guid sourceCategoryId, Guid targetCategoryId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        SpaceAccess space, CategoryMergeStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManageAsync(space, userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var categories = await store.FindPairAsync(fullWorthSpaceId, sourceCategoryId, targetCategoryId, ct);
        var source = categories.SingleOrDefault(category => category.Id == sourceCategoryId);
        var target = categories.SingleOrDefault(category => category.Id == targetCategoryId);
        if (source is null || target is null) return Results.NotFound();
        if (sourceCategoryId == targetCategoryId)
            return Results.BadRequest(new { error = "Source and target category must differ." });
        if (target.IsArchived)
            return Results.BadRequest(new { error = "Target category must be active." });

        var counts = await store.CountsAsync(fullWorthSpaceId, sourceCategoryId, ct);
        var targetIsDescendant = await store.IsDescendantAsync(fullWorthSpaceId, sourceCategoryId, targetCategoryId, ct);
        return Results.Ok(new
        {
            source = new { source.Id, source.Name, source.IsArchived, source.IsSystem },
            target = new { target.Id, target.Name },
            counts.transactions,
            counts.refundCategories,
            counts.splitAllocations,
            counts.rules,
            counts.budgets,
            counts.contracts,
            counts.purchaseItems,
            counts.productDefaults,
            counts.activeChildren,
            targetIsDescendant,
            // Solange aktive Unterkategorien darunter haengen, wuerden sie elternlos zurueckbleiben -
            // und in ein eigenes Enkelkind hinein wuerde der Baum einen Kreis schliessen.
            canApply = counts.activeChildren == 0 && !targetIsDescendant,
            canDeleteSource = counts.activeChildren == 0 && !targetIsDescendant && !source.IsSystem
        });
    }

    private static async Task<IResult> Apply(
        Guid sourceCategoryId, Guid fullWorthSpaceId, CategoryMergeApplyWrite request,
        CurrentUserContext currentUser, SpaceAccess space, CategoryMergeStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManageAsync(space, userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (sourceCategoryId == request.TargetCategoryId)
            return Results.BadRequest(new { error = "Source and target category must differ." });

        var source = await store.FindSourceAsync(fullWorthSpaceId, sourceCategoryId, ct);
        var target = await store.FindTargetAsync(fullWorthSpaceId, request.TargetCategoryId, ct);
        if (source is null || target is null) return Results.NotFound();
        if (target.IsArchived)
            return Results.BadRequest(new { error = "Target category must be active." });
        // Eine eingebaute Kategorie darf nach dem Umhaengen archiviert werden, aber nicht verschwinden:
        // sie ist Teil des Standardbaums, den eine frische Installation wieder anlegt.
        if (request.DeleteSource && source.IsSystem)
            return Results.BadRequest(new { error = "Built-in categories can be archived after reassignment but not permanently deleted." });
        // Vor jeder Zaehlung und vor jeder Bewegung: in ein eigenes Enkelkind hinein entsteht ein Kreis.
        if (await store.IsDescendantAsync(fullWorthSpaceId, sourceCategoryId, request.TargetCategoryId, ct))
            return Results.BadRequest(new { error = "Target category is a descendant of the source. Reparent it first." });

        var counts = await store.CountsAsync(fullWorthSpaceId, sourceCategoryId, ct);
        if (counts.activeChildren > 0)
            return Results.Conflict(new
            {
                error = "Move, merge or archive child categories first.",
                activeChildren = counts.activeChildren
            });

        try
        {
            await store.MergeAsync(userId, fullWorthSpaceId, source, request.TargetCategoryId, request.DeleteSource, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Conflict(new
            {
                error = "Category merge could not be completed atomically. No category references were changed."
            });
        }

        return Results.Ok(new
        {
            sourceCategoryId,
            targetCategoryId = request.TargetCategoryId,
            sourceDeleted = request.DeleteSource,
            reassigned = new
            {
                counts.transactions,
                counts.refundCategories,
                counts.splitAllocations,
                counts.rules,
                counts.budgets,
                counts.contracts,
                counts.purchaseItems,
                counts.productDefaults
            }
        });
    }

    /// <summary>Mitglied sein reicht nicht - Kategorien zu ordnen ist eine eigene Faehigkeit.</summary>
    private static async Task<bool> CanManageAsync(
        SpaceAccess space, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await space.IsMemberAsync(userId, fullWorthSpaceId, ct)
        && await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct);
}
