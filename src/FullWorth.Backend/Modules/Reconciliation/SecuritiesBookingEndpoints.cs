using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Reconciliation;

public sealed record SecuritiesBookingActionRequest(Guid PortfolioId, IReadOnlyList<Guid> TransactionIds);

/// <summary>
/// HTTP-Seite des Buchungs-Abgleichs. Wie <c>ContractDetectionService</c>/<c>ContractDetectionEndpoints</c>
/// wird bei jedem GET frisch abgeglichen statt eine Vorschlagstabelle zu fuehren - der Bestand aendert
/// sich mit jeder neuen Buchung und jedem nachgetragenen Kurs, eine gespeicherte Kandidatenliste waere
/// sofort wieder veraltet.
/// </summary>
public static class SecuritiesBookingEndpoints
{
    public static IEndpointRouteBuilder MapSecuritiesBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reconciliation").WithTags("Reconciliation");
        group.MapGet("/securities-bookings", GetSuggestions);
        group.MapPost("/securities-bookings/apply", Apply);
        group.MapPost("/securities-bookings/dismiss", Dismiss);
        return app;
    }

    private static async Task<IResult> GetSuggestions(
        Guid portfolioId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore investments,
        SecuritiesBookingStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await investments.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var suggestions = await store.ComputeSuggestionsAsync(fullWorthSpaceId, portfolioId, ct);
        if (suggestions is null) return Results.NotFound();

        return Results.Ok(new
        {
            matches = suggestions.Batch.Matches.Select(match => new
            {
                transactionId = match.TransactionId,
                securityId = match.SecurityId,
                name = suggestions.Securities.GetValueOrDefault(match.SecurityId)?.Name,
                date = match.Date,
                gross = match.Gross,
                currency = match.Currency,
                quantity = match.Quantity,
                quantityEstimated = match.QuantityEstimated,
                confident = match.Confident
            }),
            summaries = suggestions.Batch.Summaries.Select(summary => new
            {
                securityId = summary.SecurityId,
                name = suggestions.Securities.GetValueOrDefault(summary.SecurityId)?.Name,
                q = summary.CurrentQuantity,
                p = summary.CurrentCostPrice,
                sumGross = summary.SumGross,
                sumQuantity = summary.SumQuantity,
                matches = summary.BookingCount,
                confident = summary.Confident
            })
        });
    }

    private static async Task<IResult> Apply(
        SecuritiesBookingActionRequest request, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        InvestmentStore investments, SecuritiesBookingStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await investments.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.TransactionIds.Count == 0)
            return Results.BadRequest(new { error = "No transactions requested." });

        var outcome = await store.ApplyAsync(userId, fullWorthSpaceId, request.PortfolioId, request.TransactionIds, ct);
        if (outcome is null) return Results.NotFound();

        return Results.Ok(new
        {
            applied = outcome.Applied,
            alreadyApplied = outcome.AlreadyApplied,
            rejected = outcome.Rejected
        });
    }

    private static async Task<IResult> Dismiss(
        SecuritiesBookingActionRequest request, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        InvestmentStore investments, SecuritiesBookingStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await investments.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.TransactionIds.Count == 0)
            return Results.BadRequest(new { error = "No transactions requested." });

        return await store.DismissAsync(fullWorthSpaceId, request.PortfolioId, request.TransactionIds, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }
}
