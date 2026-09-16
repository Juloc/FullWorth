namespace FullWorth.Backend.Modules.Ingestion;

public static class BankingSyncStateEndpoints
{
    public static IEndpointRouteBuilder MapBankingSyncStateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/banking/connections/{connectionId:guid}/accounts/sync-state", async (
            Guid connectionId,
            string identificationHash,
            BankingSyncStateStore store,
            CancellationToken ct) =>
        {
            var matching = await store.AccountIdsWithHashAsync(connectionId, identificationHash, ct);
            if (matching.Count == 0) return Results.NotFound();
            if (matching.Count > 1)
                return Results.Conflict(new { error = "ambiguous_account_identification_hash" });

            return Results.Ok(await store.ReadSyncStateAsync(matching[0], ct));
        }).WithTags("Internal banking");

        // Die Kontenauswahl beim Verbinden fragt hier, was es schon gibt - die Zuordnung ueber den
        // IdentificationHash kann nur serverseitig entstehen.
        app.MapGet("/internal/banking/connections/{connectionId:guid}/accounts", async (
            Guid connectionId,
            BankingSyncStateStore store,
            CancellationToken ct) => Results.Ok(await store.ListForConnectionAsync(connectionId, ct)))
            .WithTags("Internal banking");

        app.MapGet("/internal/banking/transactions/{transactionId:guid}/provider-pointer", async (
            HttpContext http,
            Guid transactionId,
            Guid fullWorthSpaceId,
            BankingSyncStateStore store,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out var userId) || userId == Guid.Empty)
                return Results.BadRequest();

            var pointer = await store.FindProviderPointerAsync(transactionId, fullWorthSpaceId, userId, ct);
            return pointer is null || string.IsNullOrWhiteSpace(pointer.ProviderAccountId)
                ? Results.NotFound()
                : Results.Ok(pointer);
        }).WithTags("Internal banking");

        return app;
    }
}
