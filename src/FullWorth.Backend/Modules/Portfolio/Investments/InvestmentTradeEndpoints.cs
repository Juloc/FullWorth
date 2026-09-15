using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>
/// Einen gebuchten Handel aendern oder loeschen - die beiden Wege, die das Anlegen unter
/// <c>POST /api/investments/portfolios/{id}/trades</c> offen laesst.
///
/// Lag unter <c>/api/investment-management</c>, einer zweiten Adresse fuer dieselbe Sache: zwoelf
/// Routen, die groesstenteils nachbauten, was <c>/api/investments</c> schon konnte. Neun hatte nie
/// jemand aufgerufen und sind am 2026-09-15 weggefallen, der Kurs-Schreibweg lag doppelt vor. Was
/// bleibt, steht jetzt dort, wo alles andere zu Depots steht.
/// </summary>
public static class InvestmentTradeEndpoints
{
    private static readonly HashSet<string> TradeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "buy", "sell", "cancellation", "dividend", "interest", "fee", "tax", "deposit", "withdrawal",
        "security_transfer_in", "security_transfer_out", "split", "other"
    };

    public static IEndpointRouteBuilder MapInvestmentTradeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/investments").WithTags("Investments");
        group.MapPut("/portfolios/{portfolioId:guid}/trades/{tradeId:guid}", UpdateTrade);
        group.MapDelete("/portfolios/{portfolioId:guid}/trades/{tradeId:guid}", DeleteTrade);
        return app;
    }

    private static async Task<IResult> UpdateTrade(
        Guid portfolioId, Guid tradeId, Guid fullWorthSpaceId, InvestmentTradeWrite request,
        CurrentUserContext currentUser, InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await store.PortfolioExistsAsync(fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        var type = request.TradeType.Trim().ToLowerInvariant();
        var error = await ValidateTrade(store, fullWorthSpaceId, request, type, ct);
        if (error is not null) return Results.BadRequest(new { error });

        try
        {
            // Die Datenbank laesst keinen Bestand unter null zu. Der Versuch ist ein Konflikt, kein
            // Serverfehler - und ihre Meldung sagt genauer, welcher Handel im Weg steht.
            return await store.UpdateTradeAsync(userId, fullWorthSpaceId, portfolioId, tradeId, request, type,
                NormalizeSource(request.Source), Clean(request.ExternalKey), Clean(request.Notes), ct)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (Exception exception) when (
            exception.Message.Contains("Cannot sell", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("Cannot dispose", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("oversold", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> DeleteTrade(
        Guid portfolioId, Guid tradeId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await store.DeleteTradeAsync(userId, fullWorthSpaceId, portfolioId, tradeId, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<string?> ValidateTrade(
        InvestmentStore store, Guid fullWorthSpaceId, InvestmentTradeWrite request, string type, CancellationToken ct)
    {
        if (!TradeTypes.Contains(type)) return "Unsupported investment transaction type.";
        if (!ValidCurrency(request.Currency) || request.Amount < 0 || request.Fees < 0 || request.Taxes < 0 || request.WithholdingTax < 0)
            return "Amounts and currency are invalid.";
        if (request.SecurityId.HasValue && !await store.SecurityExistsAsync(fullWorthSpaceId, request.SecurityId.Value, ct))
            return "Security is invalid.";
        if (type is "buy" or "sell" or "cancellation" or "security_transfer_in" or "security_transfer_out" &&
            (!request.SecurityId.HasValue || request.Quantity is null or <= 0))
            return "This transaction requires a security and positive quantity.";
        if (type is "buy" or "sell" && request.Price is null or <= 0 && request.GrossAmount is null or <= 0)
            return "Buy/sell requires a positive price or gross amount.";
        if (type == "split" && (!request.SecurityId.HasValue || request.Quantity is null or <= 0))
            return "Split quantity stores the positive split ratio, e.g. 2 for 2:1.";
        return null;
    }

    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsLetter);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeSource(string? source) => string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim().ToLowerInvariant() switch
    { "manual" => "manual", "import" => "import", "provider" => "provider", _ => "manual" };
}
