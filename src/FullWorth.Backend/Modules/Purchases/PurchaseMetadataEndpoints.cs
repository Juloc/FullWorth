using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseMetadataEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var tags = app.MapGroup("/api/tags").WithTags("Purchases");
        tags.MapGet("/", async (Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => { var v = await service.ListTagsAsync(user.RequireUserId(), fullWorthSpaceId, ct); return v is null ? Results.NotFound() : Results.Ok(v); });
        tags.MapPost("/", async (Guid fullWorthSpaceId, TagWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Outcome(await service.CreateTagAsync(user.RequireUserId(), fullWorthSpaceId, request, ct), true));
        tags.MapDelete("/{tagId:guid}", async (Guid tagId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Mutation(await service.DeleteTagAsync(user.RequireUserId(), fullWorthSpaceId, tagId, ct)));
        app.MapGet("/api/purchases/{id:guid}/tags", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => { var v = await service.PurchaseTagsAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return v is null ? Results.NotFound() : Results.Ok(v); });
        app.MapPost("/api/purchases/{id:guid}/tags", async (Guid id, Guid fullWorthSpaceId, AttachTagWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Mutation(await service.AttachTagAsync(user.RequireUserId(), fullWorthSpaceId, id, request.TagId, ct)));
        app.MapDelete("/api/purchases/{id:guid}/tags/{tagId:guid}", async (Guid id, Guid tagId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Mutation(await service.DetachTagAsync(user.RequireUserId(), fullWorthSpaceId, id, tagId, ct)));
        app.MapGet("/api/purchases/warranty/upcoming", async (Guid fullWorthSpaceId, int? days, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => { var v = await service.UpcomingWarrantyAsync(user.RequireUserId(), fullWorthSpaceId, days ?? 90, ct); return v is null ? Results.NotFound() : Results.Ok(v); });
        app.MapGet("/api/purchases/{id:guid}/items/{itemId:guid}/returns", async (Guid id, Guid itemId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => { var v = await service.ItemReturnsAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, ct); return v is null ? Results.NotFound() : Results.Ok(v); });
        app.MapDelete("/api/purchases/{id:guid}/items/{itemId:guid}/returns/{returnId:guid}", async (Guid id, Guid itemId, Guid returnId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Mutation(await service.DeleteReturnAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, returnId, ct)));
        return app;
    }
    private static IResult Mutation(PurchaseMutationResult r) => r switch { PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Forbidden => Results.StatusCode(403), PurchaseMutationResult.Invalid => Results.BadRequest(), _ => Results.NotFound() };
    private static IResult Outcome((PurchaseMutationResult Result, object? Value, string? Error) o, bool created = false) => o.Result switch { PurchaseMutationResult.Success when created => Results.Created(string.Empty, o.Value), PurchaseMutationResult.Success => Results.Ok(o.Value), PurchaseMutationResult.Invalid => Results.BadRequest(new { error = o.Error }), PurchaseMutationResult.Forbidden => Results.StatusCode(403), _ => Results.NotFound() };
}
