using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Transactions;

public sealed record RefundDismissWrite(Guid OriginalTransactionId);

public static class RefundCandidateEndpoints
{
    /// <summary>Weiter zurueck als ein halbes Jahr wird keine Erstattung mehr zugeordnet.</summary>
    private const int SearchWindowDays = 180;

    public static IEndpointRouteBuilder MapRefundCandidateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/transactions/{refundId:guid}/refund-candidates", Candidates).WithTags("Transactions");
        app.MapPost("/api/transactions/{refundId:guid}/refund-candidates/dismiss", Dismiss).WithTags("Transactions");
        return app;
    }

    private static async Task<IResult> Candidates(
        Guid refundId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        RefundCandidateStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);

        var refund = await store.FindVisibleAsync(refundId, visible, ct);
        if (refund is null) return Results.NotFound();
        if (refund.Amount <= 0)
            return Results.BadRequest(new { error = "Refund candidates are available only for positive transactions." });

        var refundDate = refund.BookingDate ?? refund.ValueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var dismissed = await store.DismissedAsync(fullWorthSpaceId, refundId, ct);
        var expenses = await store.ListCandidateExpensesAsync(
            visible, refund.Currency, refundDate.AddDays(-SearchWindowDays), refundDate, dismissed, ct);
        var purchases = await store.PurchasesByTransactionAsync(fullWorthSpaceId, ct);

        return Results.Ok(RefundCandidateScoring.Rank(refund, refundDate, expenses, purchases));
    }

    private static async Task<IResult> Dismiss(
        Guid refundId, Guid fullWorthSpaceId, RefundDismissWrite request, CurrentUserContext currentUser,
        SpaceAccess space, RefundCandidateStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var writable = await space.WritableAccountIdsAsync(userId, fullWorthSpaceId, ct);

        // Beide Seiten muessen ihm gehoeren - sonst verwirft er einen Vorschlag zu fremden Buchungen.
        if (await store.FindVisibleAsync(refundId, writable, ct) is null) return Results.NotFound();
        if (await store.FindVisibleAsync(request.OriginalTransactionId, writable, ct) is null) return Results.NotFound();

        await store.DismissAsync(userId, fullWorthSpaceId, refundId, request.OriginalTransactionId, ct);
        return Results.NoContent();
    }
}
