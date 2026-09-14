using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/products").WithTags("Products");
        group.MapGet("/", async (Guid fullWorthSpaceId, string? query, Guid? categoryId, string? brand, bool? includeArchived, int? offset, int? limit, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var value = await service.ListAsync(user.RequireUserId(), fullWorthSpaceId, query, categoryId, brand, includeArchived == true, offset ?? 0, limit ?? 100, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var value = await service.GetAsync(user.RequireUserId(), fullWorthSpaceId, id, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapPost("/", async (Guid fullWorthSpaceId, ProductWrite request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) => Map(await service.CreateAsync(user.RequireUserId(), fullWorthSpaceId, request, ct), true));
        group.MapPatch("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, ProductWrite request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) => Map(await service.UpdateAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct)));
        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
            (await service.ArchiveAsync(user.RequireUserId(), fullWorthSpaceId, id, true, ct)) == PurchaseMutationResult.Success ? Results.NoContent() : Results.NotFound());
        group.MapPost("/{id:guid}/restore", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
            (await service.ArchiveAsync(user.RequireUserId(), fullWorthSpaceId, id, false, ct)) == PurchaseMutationResult.Success ? Results.NoContent() : Results.NotFound());
        group.MapGet("/{id:guid}/history", async (Guid id, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var value = await service.HistoryAsync(user.RequireUserId(), fullWorthSpaceId, id, from, to, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapGet("/match", async (Guid fullWorthSpaceId, string? barcode, string? name, string? brand, Guid? merchantId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var value = await service.MatchAsync(user.RequireUserId(), fullWorthSpaceId, barcode, name, brand, merchantId, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        group.MapPost("/{id:guid}/aliases", async (Guid id, Guid fullWorthSpaceId, ProductAliasWrite request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) => Map(await service.AddAliasAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapDelete("/{id:guid}/aliases/{aliasId:guid}", async (Guid id, Guid aliasId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
            (await service.RemoveAliasAsync(user.RequireUserId(), fullWorthSpaceId, id, aliasId, ct)) == PurchaseMutationResult.Success ? Results.NoContent() : Results.NotFound());
        group.MapPost("/{id:guid}/barcodes", async (Guid id, Guid fullWorthSpaceId, ProductBarcodeWrite request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) => Map(await service.AddBarcodeAsync(user.RequireUserId(), fullWorthSpaceId, id, request, ct), true));
        group.MapDelete("/{id:guid}/barcodes/{barcodeId:guid}", async (Guid id, Guid barcodeId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
            (await service.RemoveBarcodeAsync(user.RequireUserId(), fullWorthSpaceId, id, barcodeId, ct)) == PurchaseMutationResult.Success ? Results.NoContent() : Results.NotFound());
        group.MapPost("/merge", async (Guid fullWorthSpaceId, ProductMergeRequest request, FullWorth.Backend.Security.CurrentUserContext user, ProductService service, CancellationToken ct) =>
        {
            var result = await service.MergeAsync(user.RequireUserId(), fullWorthSpaceId, request, ct);
            return result.Result switch
            {
                PurchaseMutationResult.Success => Results.NoContent(),
                PurchaseMutationResult.Invalid => Results.BadRequest(new { error = result.Error }),
                _ => Results.NotFound()
            };
        });
        return app;
    }

    private static IResult Map((PurchaseMutationResult Result, object? Value, string? Error) outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created => Results.Created(string.Empty, outcome.Value),
        PurchaseMutationResult.Success => Results.Ok(outcome.Value),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }),
        PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.NotFound()
    };
}
