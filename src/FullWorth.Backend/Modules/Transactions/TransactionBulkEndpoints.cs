using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Transactions;

public sealed record TransactionBulkFilter(
    IReadOnlyList<Guid>? AccountIds = null,
    IReadOnlyList<Guid>? CategoryIds = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Direction = null,
    string? Query = null,
    bool? IsIgnored = null,
    bool? IsTransfer = null,
    bool? IsPending = null);

public sealed record TransactionBulkMutation(
    TransactionBulkFilter Filter,
    bool UpdateCategory = false,
    Guid? CategoryId = null,
    bool? IsIgnored = null,
    bool? IsReviewed = null,
    Guid? ContractId = null,
    bool ClearContract = false,
    string? ReplaceNote = null,
    bool ConfirmReplaceNotes = false);

/// <summary>Massenaenderungen an Buchungen - Vorschau und Ausfuehrung. Kam aus Parity.</summary>
public static class TransactionBulkEndpoints
{
    public static IEndpointRouteBuilder MapTransactionBulkEndpoints(this IEndpointRouteBuilder app)
    {
        var bulk = app.MapGroup("/api/transaction-bulk").WithTags("Transactions");
        bulk.MapPost("/preview", PreviewBulk);
        bulk.MapPost("/execute", ExecuteBulk);
        return app;
    }

    private static async Task<IResult> PreviewBulk(
        Guid fullWorthSpaceId, TransactionBulkFilter request, CurrentUserContext currentUser,
        SpaceAccess space, TransactionBulkStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);
        var (count, samples) = await store.PreviewAsync(writable, request, ct);
        return Results.Ok(new
        {
            count,
            samples = samples.Select(sample => new
            {
                sample.Id,
                date = sample.Date,
                sample.Counterparty,
                sample.Amount,
                sample.Currency,
                sample.CategoryId
            }),
            capped = count > TransactionBulkStore.MaxMatches
        });
    }

    private static async Task<IResult> ExecuteBulk(
        Guid fullWorthSpaceId, TransactionBulkMutation request, CurrentUserContext currentUser,
        SpaceAccess space, TransactionBulkStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // Notizen zu ersetzen loescht, was der Benutzer selbst geschrieben hat - das passiert nur auf
        // ausdrueckliche Bestaetigung.
        if (request.ReplaceNote is not null && !request.ConfirmReplaceNotes)
            return Results.BadRequest(new { error = "Replacing notes requires explicit confirmation." });
        if (request.UpdateCategory && request.CategoryId.HasValue &&
            !await store.CategoryExistsAsync(fullWorthSpaceId, request.CategoryId.Value, ct))
            return Results.BadRequest(new { error = "Category is invalid." });
        if (request.ContractId.HasValue &&
            !await store.ActiveContractExistsAsync(fullWorthSpaceId, request.ContractId.Value, ct))
            return Results.BadRequest(new { error = "Contract is invalid." });

        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);

        var ids = await store.MatchingIdsAsync(writable, request.Filter, ct);
        if (ids.Count > TransactionBulkStore.MaxMatches)
            return Results.BadRequest(new { error = "More than 10000 transactions match. Narrow the filters." });
        if (ids.Count == 0) return Results.Ok(new { updated = 0 });

        return Results.Ok(new { updated = await store.ApplyAsync(userId, fullWorthSpaceId, ids, request, ct) });
    }
}
