namespace FullWorth.Backend.Modules.Ingestion;

public sealed record FinTsHoldingSnapshotItem(
    string ProviderKey,
    string Name,
    string? Isin,
    string? Wkn,
    string Currency,
    decimal Quantity,
    decimal? Price,
    DateOnly? PriceDate,
    decimal? MarketValue,
    string? Exchange,
    /// <summary>Einstandskurs JE STUECK, wenn die Bank ihn nennt (MT535 :70E:). Nie der Wert.</summary>
    decimal? CostPrice = null);

public sealed record FinTsInvestmentSnapshotRequest(
    Guid ConnectionId,
    string DepotKey,
    string Name,
    string Currency,
    DateOnly AsOf,
    IReadOnlyList<FinTsHoldingSnapshotItem> Holdings);

public static class FinTsInvestmentSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapFinTsInvestmentSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/banking/fints/investment-snapshot", IngestAsync)
            .WithTags("Internal banking");
        return app;
    }

    private static async Task<IResult> IngestAsync(
        FinTsInvestmentSnapshotRequest request,
        FinTsInvestmentSnapshotStore store,
        ILogger<FinTsInvestmentSnapshotRequest> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DepotKey) || string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3)
            return Results.BadRequest();

        var spaceId = await store.FindSpaceOfConnectionAsync(request.ConnectionId, ct);
        if (spaceId is null) return Results.NotFound();

        var outcome = await store.ApplyAsync(spaceId.Value, request, ct);
        // Ein weggeworfenes Wertpapier ist eine Meldung wert. Gezaehlt wird, nicht benannt: Namen und
        // ISINs gehoeren dem Eigentuemer, die Zahl reicht, um den Verlust zu finden.
        if (outcome.SkippedWithoutQuantity > 0 || outcome.SkippedWithoutIdentity > 0)
            logger.LogWarning(
                "FinTS depot snapshot kept {Kept} of {Sent} holdings. WithoutQuantity={NoQuantity}, WithoutIdentity={NoIdentity}",
                outcome.Positions, request.Holdings.Count,
                outcome.SkippedWithoutQuantity, outcome.SkippedWithoutIdentity);
        return Results.Ok(new
        {
            portfolioId = outcome.PortfolioId,
            positions = outcome.Positions,
            skippedWithoutQuantity = outcome.SkippedWithoutQuantity,
            skippedWithoutIdentity = outcome.SkippedWithoutIdentity,
        });
    }
}
