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
    string? Exchange);

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
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DepotKey) || string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3)
            return Results.BadRequest();

        var spaceId = await store.FindSpaceOfConnectionAsync(request.ConnectionId, ct);
        if (spaceId is null) return Results.NotFound();

        var outcome = await store.ApplyAsync(spaceId.Value, request, ct);
        return Results.Ok(new { portfolioId = outcome.PortfolioId, positions = outcome.Positions });
    }
}
