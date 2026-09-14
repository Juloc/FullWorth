using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Categories;

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var categories = app.MapGroup("/api/categories").WithTags("Categories");
        categories.MapGet("/", async (Guid fullWorthSpaceId, bool? includeArchived, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
        {
            var result = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct, includeArchived ?? false);
            return result.Found ? Results.Ok(result.Items) : Results.NotFound();
        });
        categories.MapPost("/", async (Guid fullWorthSpaceId, CategoryWrite request, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
            Mutation(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));
        categories.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CategoryUpdate request, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
            Mutation(await store.UpdateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        categories.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
        {
            var outcome = await store.ArchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return outcome.Result switch
            {
                CategoryMutationResult.Success => Results.NoContent(),
                CategoryMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                CategoryMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid request." }),
                _ => Results.NotFound()
            };
        });
        categories.MapPost("/{id:guid}/restore", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
            Mutation(await store.UnarchiveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        var rules = app.MapGroup("/api/categorization-rules").WithTags("Categorization rules");
        rules.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
        {
            var result = await store.ListRulesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result.Found ? Results.Ok(result.Items) : Results.NotFound();
        });
        rules.MapPost("/", async (Guid fullWorthSpaceId, RuleWrite request, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
            Mutation(await store.UpsertRuleForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, null, request, ct)));
        rules.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, RuleWrite request, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
            Mutation(await store.UpsertRuleForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        rules.MapPost("/reapply", async (Guid fullWorthSpaceId, bool? apply, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
            Mutation(await store.ReapplyRulesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, apply ?? false, ct)));
        rules.MapPost("/preview", async (Guid fullWorthSpaceId, RuleWrite request, CurrentUserContext currentUser, CategoryStore store, CancellationToken ct) =>
            Mutation(await store.PreviewRuleForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));
        return app;
    }

    private static IResult Mutation<T>(CategoryMutationOutcome<T> outcome) => outcome.Result switch
    {
        CategoryMutationResult.Success when outcome.Value is not null => Results.Ok(outcome.Value),
        CategoryMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        CategoryMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid request." }),
        _ => Results.NotFound()
    };
}
