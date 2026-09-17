using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.BankConnections;

public static class BankConnectionEndpoints
{
    public static IEndpointRouteBuilder MapBankConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bank-connections").WithTags("Bank connections");
        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, BankConnectionStore store, CancellationToken ct) =>
        {
            var items = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return items is null ? Results.NotFound() : Results.Ok(items);
        });
        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, BankConnectionStore store, CancellationToken ct) =>
        {
            var item = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapGet("/{id:guid}/sync-history", async (
            Guid id,
            Guid fullWorthSpaceId,
            int? limit,
            CurrentUserContext currentUser,
            BankConnectionStore store,
            CancellationToken ct) =>
        {
            var items = await store.ListSyncHistoryForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, id, limit ?? 10, ct);
            return items is null ? Results.NotFound() : Results.Ok(items);
        });

        // Was die Bank zuletzt geschickt hat. Der Eigentuemer darf seine eigene Antwort sehen -
        // ohne sie bleibt jede Frage nach einem fehlenden Wertpapier ein Ratespiel mit Release.
        group.MapGet("/{id:guid}/raw-responses", async (
            Guid id,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FinTsRawResponseStore store,
            CancellationToken ct) =>
        {
            var items = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return items is null ? Results.NotFound() : Results.Ok(items);
        });

        group.MapGet("/{id:guid}/raw-responses/{rawId:guid}", async (
            Guid id,
            Guid rawId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FinTsRawResponseStore store,
            CancellationToken ct) =>
        {
            var item = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, rawId, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        // No public mutation endpoint here. Disconnect must pass through FullWorth.Banking so the
        // provider session/consent is closed before local data is destroyed.

        // Internal machine-to-machine Banking ingest path. Authentication is enforced separately for /internal/**.
        var internalGroup = app.MapGroup("/internal/banking/connections").WithTags("Internal banking");
        internalGroup.MapGet("/", async (BankConnectionStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));
        internalGroup.MapPost("/", async (BankConnectionWrite request, BankConnectionStore store, CancellationToken ct) =>
        {
            try { return Results.Ok(await store.UpsertAsync(request, ct)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        internalGroup.MapPost("/{id:guid}/sync-history", async (
            Guid id,
            BankSyncHistoryWrite request,
            BankConnectionStore store,
            CancellationToken ct) =>
            await store.RecordSyncHistoryAsync(id, request, ct)
                ? Results.NoContent()
                : Results.NotFound());
        internalGroup.MapPost("/{id:guid}/raw-responses", async (
            Guid id,
            FinTsRawResponseWrite request,
            FinTsRawResponseStore store,
            CancellationToken ct) =>
            await store.RecordAsync(id, request, ct) ? Results.NoContent() : Results.NotFound());

        // One-time atomic state consumption (replaces the replayable read-only by-state lookup).
        internalGroup.MapPost("/consume-state", async (ConsumeStateRequest request, BankConnectionStore store, CancellationToken ct) =>
        {
            var item = await store.ConsumeAuthorizationStateAsync(request.State, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });
        // P0.2 owner authorization for the Banking service (connect + manual sync). The trusted user id
        // arrives in X-FullWorth-User-Id; /internal is already gated by the ingest key.
        internalGroup.MapPost("/authorize", async (HttpContext http, BankConnectionAuthorizeRequest request, BankConnectionStore store, CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out var userId))
                return Results.BadRequest();
            return await store.AuthorizeAsync(userId, request.FullWorthSpaceId, request.ConnectionId, request.EnableBankingProfileId, ct) switch
            {
                BankConnectionAuthorizeResult.Authorized => Results.NoContent(),
                BankConnectionAuthorizeResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        });
        // Banking closes the remote Enable Banking session first. This path deliberately does NOT
        // use UpsertAsync so an intentional user disconnect cannot emit a bank_reauth transition push.
        internalGroup.MapPost("/{id:guid}/close-retain", async (
            HttpContext http,
            Guid id,
            CloseBankConnectionInternalRequest request,
            BankConnectionStore store,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out var userId))
                return Results.BadRequest();
            return await store.CloseRetainingDataForUserAsync(userId, request.FullWorthSpaceId, id, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        // Banking closes the remote Enable Banking session first, then calls this local destructive delete.
        internalGroup.MapPost("/{id:guid}/delete", async (
            HttpContext http,
            Guid id,
            DeleteBankConnectionInternalRequest request,
            BankConnectionStore store,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out var userId))
                return Results.BadRequest();
            return await store.DeleteForUserAsync(userId, request.FullWorthSpaceId, id, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });
        return app;
    }
}
