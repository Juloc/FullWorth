using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Categories;

public sealed record CategoryLanguageWrite(string Language);

/// <summary>
/// Die Sprache der Standardkategorien lesen und setzen - die Frage, die der Einrichtungsassistent
/// stellt. Eigentuemersache, weil sie den ganzen Space betrifft.
/// </summary>
public static class CategoryLanguageEndpoints
{
    public static IEndpointRouteBuilder MapCategoryLanguageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/categories/language", Read).WithTags("Categories");
        app.MapPut("/api/categories/language", Apply).WithTags("Categories");
        return app;
    }

    private static async Task<IResult> Read(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        CategoryLanguageStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var state = await store.ReadAsync(fullWorthSpaceId, ct);
        return state is null ? Results.NotFound() : Results.Ok(state);
    }

    private static async Task<IResult> Apply(
        Guid fullWorthSpaceId, CategoryLanguageWrite request, CurrentUserContext currentUser,
        SpaceAccess space, CategoryLanguageStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await space.IsOwnerAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await store.ApplyAsync(fullWorthSpaceId, request.Language, ct) switch
        {
            CategoryLanguageResult.Applied or CategoryLanguageResult.NoChange =>
                Results.Ok(await store.ReadAsync(fullWorthSpaceId, ct)),
            // Wer eine Standardkategorie umbenannt hat, hat entschieden. Das ueberschreibt keine
            // Spracheinstellung.
            CategoryLanguageResult.Locked => Results.Conflict(new
            {
                error = "Default categories were renamed; the language can no longer be switched automatically."
            }),
            _ => Results.NotFound()
        };
    }
}
