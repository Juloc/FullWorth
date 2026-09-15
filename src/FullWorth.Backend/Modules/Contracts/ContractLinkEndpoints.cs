using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Contracts;

public sealed record ContractLinkWrite(Guid TransactionId, decimal Amount, string LinkSource = "manual", decimal? Confidence = null);

/// <summary>
/// Welche Buchung zu welchem Vertrag gehoert - von beiden Seiten lesbar, weil beide Seiten danach
/// fragen: die Vertragsseite will die Zahlungen eines Vertrags, die Buchungsliste will wissen, auf
/// welche Vertraege eine einzelne Zahlung verteilt ist.
/// </summary>
public static class ContractLinkEndpoints
{
    public static IEndpointRouteBuilder MapContractLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts").WithTags("Contracts");
        group.MapGet("/{contractId:guid}/links", GetContractLinks);
        group.MapPost("/{contractId:guid}/links", AddContractLink);
        group.MapDelete("/{contractId:guid}/links/{linkId:guid}", DeleteContractLink);
        group.MapGet("/transaction/{transactionId:guid}/links", GetTransactionLinks);
        return app;
    }

    private static async Task<IResult> GetContractLinks(
        Guid contractId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.CanReadContract(uid, fullWorthSpaceId, contractId, ct)) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(uid, fullWorthSpaceId, ct);
        var rows = (await store.LinksOfContractAsync(contractId, fullWorthSpaceId, ct))
            .Where(row => visible.Contains(row.AccountId))
            .Select(row => new
            {
                id = row.Id,
                transactionId = row.TransactionId,
                amount = row.Amount,
                linkSource = row.LinkSource,
                confidence = row.Confidence,
                date = row.Date,
                counterparty = row.Counterparty,
                transactionAmount = row.TransactionAmount,
                currency = row.Currency
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetTransactionLinks(
        Guid transactionId, Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        var visible = await space.VisibleAccountIdsAsync(uid, fullWorthSpaceId, ct);
        if (await store.FindTransactionAsync(transactionId, visible, ct) is null) return Results.NotFound();

        var rows = (await store.ContractsOfTransactionAsync(transactionId, fullWorthSpaceId, ct))
            .Where(row => !row.AccountId.HasValue || visible.Contains(row.AccountId.Value))
            .Select(row => new
            {
                id = row.Id,
                contractId = row.ContractId,
                amount = row.Amount,
                linkSource = row.LinkSource,
                name = row.Name,
                currency = row.Currency
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> AddContractLink(
        Guid contractId, Guid fullWorthSpaceId, ContractLinkWrite request, CurrentUserContext currentUser,
        SpaceAccess space, ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.CanWriteContract(uid, fullWorthSpaceId, contractId, ct)) return Results.StatusCode(403);

        var writable = await space.WritableAccountIdsAsync(uid, fullWorthSpaceId, ct);
        var transaction = await store.FindTransactionAsync(request.TransactionId, writable, ct);
        if (transaction is null) return Results.NotFound();
        if (transaction.Amount >= 0 || request.Amount <= 0)
            return Results.BadRequest(new { error = "Only expense transactions can be linked as contract payments." });

        // Eine Buchung kann auf mehrere Vertraege verteilt sein, aber nie ueber ihren Betrag hinaus.
        var allocated = await store.AllocatedAmountAsync(request.TransactionId, ct);
        if (allocated + request.Amount > Math.Abs(transaction.Amount) + 0.01m)
            return Results.BadRequest(new { error = "Contract link amounts exceed the transaction amount." });

        var id = await store.AddLinkAsync(uid, fullWorthSpaceId, contractId, request.TransactionId,
            request.Amount, NormalizeSource(request.LinkSource), request.Confidence, ct);
        return Results.Ok(new { id });
    }

    private static async Task<IResult> DeleteContractLink(
        Guid contractId, Guid linkId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        // Zwei Rechte: der Vertrag UND das Konto der Buchung, die daran haengt.
        if (!await store.CanWriteContract(uid, fullWorthSpaceId, contractId, ct)
            || !await store.CanWriteContractLinkAsync(uid, fullWorthSpaceId, contractId, linkId, ct))
            return Results.StatusCode(403);

        return await store.DeleteLinkAsync(uid, fullWorthSpaceId, contractId, linkId, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static string NormalizeSource(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "detection" => "detection",
        "import" => "import",
        _ => "manual"
    };
}
