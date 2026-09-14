using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Merchants;

public static class MerchantEndpoints
{
    public static IEndpointRouteBuilder MapMerchantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/merchants").WithTags("Merchants");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, MerchantStore store, CancellationToken ct) =>
        {
            var result = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result.Found ? Results.Ok(result.Items) : Results.NotFound();
        });

        group.MapGet("/resolve", async (Guid fullWorthSpaceId, string? counterparty, CurrentUserContext currentUser, MerchantStore store, CancellationToken ct) =>
        {
            var result = await store.ResolveForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, counterparty, ct);
            return result.Found ? Results.Ok(result.View) : Results.NotFound();
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, MerchantWrite request, CurrentUserContext currentUser, MerchantStore store, CancellationToken ct) =>
            Mutation(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct)));

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, MerchantWrite request, CurrentUserContext currentUser, MerchantStore store, CancellationToken ct) =>
            Mutation(await store.RenameForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapPost("/{id:guid}/merge", async (Guid id, Guid fullWorthSpaceId, MerchantMergeWrite request, CurrentUserContext currentUser, MerchantStore store, CancellationToken ct) =>
            Mutation(await store.MergeForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, MerchantStore store, CancellationToken ct) =>
            Status(await store.DeleteForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)));

        group.MapPost("/{id:guid}/aliases", async (Guid id, Guid fullWorthSpaceId, AliasWrite request, CurrentUserContext currentUser, MerchantStore store, CancellationToken ct) =>
            Mutation(await store.AddAliasForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)));

        group.MapDelete("/{id:guid}/aliases/{aliasId:guid}", async (Guid id, Guid aliasId, Guid fullWorthSpaceId, CurrentUserContext currentUser, MerchantStore store, CancellationToken ct) =>
            Status(await store.RemoveAliasForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, aliasId, ct)));

        return app;
    }

    private static IResult Mutation<T>(MerchantOutcome<T> outcome) => outcome.Result switch
    {
        MerchantResult.Success when outcome.Value is not null => Results.Ok(outcome.Value),
        MerchantResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        MerchantResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid request." }),
        _ => Results.NotFound()
    };

    private static IResult Status(MerchantResult result) => result switch
    {
        MerchantResult.Success => Results.NoContent(),
        MerchantResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        MerchantResult.Invalid => Results.BadRequest(),
        _ => Results.NotFound()
    };
}
