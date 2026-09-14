using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Categories;

public static class CategoryOrderEndpoints
{
    public static IEndpointRouteBuilder MapCategoryOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/category-order", Apply).WithTags("Categories");
        return app;
    }

    private static async Task<IResult> Apply(
        Guid fullWorthSpaceId,
        CategoryOrderApplyWrite request,
        CurrentUserContext currentUser,
        SpaceAccess space,
        CategoryOrderService order,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // Was sich an der Anfrage selbst entscheidet, entscheidet sich hier - dafuer muss niemand die
        // Datenbank fragen.
        var items = (request.Items ?? []).ToArray();
        if (items.Length == 0) return Results.Ok(new { changed = 0 });
        if (items.Length > CategoryOrderService.MaxItems)
            return Results.BadRequest(new { error = "At most 500 categories can be reordered at once." });
        if (items.Select(item => item.Id).Distinct().Count() != items.Length)
            return Results.BadRequest(new { error = "Each category can appear only once." });
        if (items.Any(item => item.SortOrder is < 0 or > 1_000_000))
            return Results.BadRequest(new { error = "Category sort order is out of range." });
        if (items.Any(item => item.ParentId == item.Id))
            return Results.BadRequest(new { error = "A category cannot be its own parent." });

        var outcome = await order.ApplyAsync(userId, fullWorthSpaceId, items, ct);
        return outcome.Result switch
        {
            CategoryOrderResult.Applied => Results.Ok(new { changed = outcome.Changed }),
            CategoryOrderResult.NotFound => Results.NotFound(),
            CategoryOrderResult.Invalid => Results.BadRequest(new { error = outcome.Error }),
            _ => Results.Conflict(new { error = outcome.Error })
        };
    }
}
