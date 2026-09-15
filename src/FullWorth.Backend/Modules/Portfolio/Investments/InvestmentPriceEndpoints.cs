using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed record InvestmentPriceWrite(
    Guid SecurityId, DateOnly PriceDate, decimal Price, string Currency, string Source = "manual");

/// <summary>
/// Einen Kurs von Hand erfassen. Alles andere zu Kursen - abrufen, Historie, Anbieter aktualisieren -
/// liegt unter <c>/api/market-data</c>; das hier ist der eine Schreibweg.
///
/// Es gab ihn zweimal: einmal unter <c>/api/investments/prices</c> und einmal unter
/// <c>/api/investment-management/prices</c>, beide auf dieselbe Store-Methode. Die Oberflaeche rief
/// den zweiten auf, und nur der zweite pruefte die Waehrung wirklich (drei BUCHSTABEN, nicht drei
/// beliebige Zeichen) und begrenzte die Quelle auf die Spaltenlaenge. Geblieben ist der strengere
/// unter der Adresse des schwaecheren.
/// </summary>
public static class InvestmentPriceEndpoints
{
    public static IEndpointRouteBuilder MapInvestmentPriceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/investments/prices", PutPrice).WithTags("Investments");
        return app;
    }

    private static async Task<IResult> PutPrice(
        Guid fullWorthSpaceId, InvestmentPriceWrite request, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.Price <= 0 || !ValidCurrency(request.Currency)
            || !await store.SecurityExistsAsync(fullWorthSpaceId, request.SecurityId, ct))
            return Results.BadRequest(new { error = "Security, positive price and valid currency are required." });

        var source = string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim().ToLowerInvariant();
        if (source.Length > 64) return Results.BadRequest(new { error = "Price source is too long." });

        await store.SavePriceAsync(userId, fullWorthSpaceId, request.SecurityId, request.PriceDate, request.Price,
            request.Currency.Trim().ToUpperInvariant(), source, ct);
        return Results.NoContent();
    }

    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsLetter);
}
