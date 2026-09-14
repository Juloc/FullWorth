using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases").WithTags("Purchases");
        group.MapGet("/", async (Guid? transactionId, string? source, DateOnly? from, DateOnly? to, PurchaseStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(transactionId, source, from, to, ct)));
        group.MapGet("/{id:guid}", async (Guid id, PurchaseStore store, CancellationToken ct) => { var x = await store.GetAsync(id, ct); return x is null ? Results.NotFound() : Results.Ok(x); });
        group.MapPost("/", async (PurchaseWrite request, PurchaseStore store, CancellationToken ct) => Results.Ok(await store.UpsertAsync(null, request, ct)));
        group.MapPut("/{id:guid}", async (Guid id, PurchaseWrite request, PurchaseStore store, CancellationToken ct) => Results.Ok(await store.UpsertAsync(id, request, ct)));
        group.MapPut("/{id:guid}/items", async (Guid id, List<PurchaseItemWrite> items, PurchaseStore store, CancellationToken ct) => { await store.ReplaceItemsAsync(id, items, ct); return Results.NoContent(); });
        group.MapGet("/{id:guid}/match-candidates", async (Guid id, PurchaseStore store, CancellationToken ct) => Results.Ok(await store.MatchCandidatesAsync(id, ct)));
        group.MapPost("/{id:guid}/link", async (Guid id, LinkPurchaseRequest request, PurchaseStore store, CancellationToken ct) => { await store.LinkAsync(id, request.TransactionId, request.Confidence, ct); return Results.NoContent(); });
        return app;
    }
}