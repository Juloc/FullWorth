using System.Text;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Contracts;

public sealed record ContractLinkWrite(Guid TransactionId, decimal Amount, string LinkSource = "manual", decimal? Confidence = null);
public sealed record ContractSplitComponent(string Name, decimal Amount, Guid? CategoryId, string? Kind);
public sealed record ContractSplitWrite(string BundleName, IReadOnlyList<ContractSplitComponent> Components, string HistoryMode = "from_now");
public sealed record ContractMergeWrite(IReadOnlyList<Guid> ContractIds, Guid? TargetContractId = null, string? TargetName = null, Guid? TargetCategoryId = null, Guid? TargetAccountId = null);
public sealed record CancellationWrite(DateOnly? MinimumTermEnd,int? NoticePeriodValue,string? NoticePeriodUnit,int? RenewalPeriodValue,string? RenewalPeriodUnit,bool AutoRenews,DateOnly? CancellationDeadline,string CancellationStatus,string? CustomerNumber,string? ProviderContact);
public sealed record CancellationDetailsView(
    DateOnly? MinimumTermEnd,
    int? NoticePeriodValue,
    string? NoticePeriodUnit,
    int? RenewalPeriodValue,
    string? RenewalPeriodUnit,
    bool AutoRenews,
    DateOnly? CancellationDeadline,
    string CancellationStatus,
    string? CustomerNumber,
    string? ProviderContact,
    DateTimeOffset? CancellationSentAt,
    DateTimeOffset? CancellationConfirmedAt,
    DateTimeOffset? UpdatedAt);

public static class ContractParityEndpoints
{
    public static IEndpointRouteBuilder MapContractParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/contract-parity").WithTags("Contracts");
        group.MapGet("/{contractId:guid}/links",GetContractLinks);
        group.MapPost("/{contractId:guid}/links",AddContractLink);
        group.MapDelete("/{contractId:guid}/links/{linkId:guid}",DeleteContractLink);
        group.MapGet("/transaction/{transactionId:guid}/links",GetTransactionLinks);
        group.MapPost("/{contractId:guid}/split",SplitContract);
        group.MapDelete("/merge/{targetContractId:guid}/{sourceContractId:guid}",UnmergeContracts);
        group.MapGet("/cancellations",ListCancellations);
        group.MapGet("/{contractId:guid}/cancellation",GetCancellation);
        group.MapPut("/{contractId:guid}/cancellation",PutCancellation);
        group.MapGet("/{contractId:guid}/cancellation-letter",CancellationLetter);
        group.MapGet("/cancellation-deadlines",CancellationDeadlines);
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

    private static async Task<IResult> SplitContract(
        Guid contractId, Guid fullWorthSpaceId, ContractSplitWrite request, CurrentUserContext currentUser,
        ContractStore contracts, ContractLinkStore links, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await links.CanWriteContract(uid, fullWorthSpaceId, contractId, ct)) return Results.StatusCode(403);

        var parent = await contracts.FindInSpaceAsync(fullWorthSpaceId, contractId, ct);
        if (parent is null) return Results.NotFound();

        var components = (request.Components ?? [])
            .Where(part => !string.IsNullOrWhiteSpace(part.Name) && part.Amount > 0)
            .ToArray();
        if (components.Length < 2 || Math.Abs(components.Sum(part => part.Amount) - parent.Amount) > 0.01m)
            return Results.BadRequest(new { error = "Split components must contain at least two rows and equal the expected contract amount." });

        foreach (var part in components)
            if (part.CategoryId.HasValue
                && !await contracts.CategoryBelongsToSpaceAsync(fullWorthSpaceId, part.CategoryId.Value, ct))
                return Results.BadRequest(new { error = "Split contains an invalid category." });

        // Die Historie mitzunehmen heisst, fremde Buchungen anzufassen - dafuer reicht das
        // Vertragsrecht allein nicht, es braucht auch das Recht an jedem beteiligten Konto.
        var copyHistory = string.Equals(request.HistoryMode, "same_split", StringComparison.OrdinalIgnoreCase);
        if (copyHistory && !await links.AllContractLinksWritableAsync(uid, fullWorthSpaceId, contractId, ct))
            return Results.StatusCode(403);

        var (bundleId, children) = await contracts.SplitAsync(
            uid, fullWorthSpaceId, parent, request.BundleName, components, copyHistory, ct);

        return Results.Ok(new
        {
            bundleId,
            archivedContractId = contractId,
            contracts = children.Select(child => new { child.Id, child.Name, child.Amount })
        });
    }

    private static async Task<IResult> UnmergeContracts(
        Guid targetContractId,
        Guid sourceContractId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        ContractStore store,
        CancellationToken ct)
    {
        var result = await store.UnmergeForUserAsync(
            currentUser.RequireUserId(),
            fullWorthSpaceId,
            targetContractId,
            sourceContractId,
            ct);

        return result switch
        {
            ContractMutationResult.Success => Results.NoContent(),
            ContractMutationResult.NotFound => Results.NotFound(),
            ContractMutationResult.Forbidden => Results.StatusCode(403),
            ContractMutationResult.Invalid => Results.BadRequest(),
            _ => Results.StatusCode(409)
        };
    }

    private static async Task<IResult> ListCancellations(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(uid, fullWorthSpaceId, ct)) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(uid, fullWorthSpaceId, ct);
        var rows = (await store.ListCancellationsAsync(fullWorthSpaceId, ct))
            .Where(row => !row.AccountId.HasValue || visible.Contains(row.AccountId.Value))
            .Select(row => new
            {
                contractId = row.ContractId,
                minimumTermEnd = row.MinimumTermEnd,
                cancellationDeadline = row.CancellationDeadline,
                cancellationStatus = row.CancellationStatus,
                autoRenews = row.AutoRenews,
                cancellationSentAt = row.CancellationSentAt,
                cancellationConfirmedAt = row.CancellationConfirmedAt
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetCancellation(
        Guid contractId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.CanReadContract(uid, fullWorthSpaceId, contractId, ct)) return Results.NotFound();

        // Ohne Eintrag ist nichts gekuendigt - das ist eine gueltige Antwort, kein Fehlen.
        return Results.Ok(await store.ReadCancellationDetailsAsync(contractId, ct)
            ?? new CancellationDetailsView(null, null, null, null, null, false, null, "none", null, null, null, null, null));
    }

    private static async Task<IResult> PutCancellation(
        Guid contractId, Guid fullWorthSpaceId, CancellationWrite request, CurrentUserContext currentUser,
        ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.CanWriteContract(uid, fullWorthSpaceId, contractId, ct)) return Results.StatusCode(403);
        if (request.NoticePeriodValue < 0 || request.RenewalPeriodValue < 0
            || request.CancellationStatus is not ("none" or "planned" or "sent" or "confirmed" or "cancelled"))
            return Results.BadRequest(new { error = "Invalid cancellation metadata." });

        // Eine mitgeschickte Frist gewinnt; sonst wird sie aus Mindestlaufzeit und Kuendigungsfrist gerechnet.
        var deadline = request.CancellationDeadline
            ?? CalculateDeadline(request.MinimumTermEnd, request.NoticePeriodValue, request.NoticePeriodUnit);
        var now = DateTimeOffset.UtcNow;
        var sent = request.CancellationStatus is "sent" or "confirmed" or "cancelled" ? now : (DateTimeOffset?)null;
        var confirmed = request.CancellationStatus is "confirmed" or "cancelled" ? now : (DateTimeOffset?)null;

        await store.SaveCancellationAsync(uid, fullWorthSpaceId, contractId, request, deadline, sent, confirmed,
            NormalizeUnit(request.NoticePeriodUnit), NormalizeUnit(request.RenewalPeriodUnit), ct);
        return Results.Ok(new { deadline, status = request.CancellationStatus });
    }

    private static async Task<IResult> CancellationLetter(
        Guid contractId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.CanReadContract(uid, fullWorthSpaceId, contractId, ct)) return Results.NotFound();

        var contract = await store.ContractAsync(contractId, ct);
        var details = await store.ReadCancellation(contractId, ct);

        var letter = new StringBuilder();
        letter.AppendLine("Kündigung meines Vertrags").AppendLine()
            .AppendLine($"Anbieter: {contract.ProviderName ?? contract.Name}");
        if (!string.IsNullOrWhiteSpace(details.CustomerNumber))
            letter.AppendLine($"Kunden-/Vertragsnummer: {details.CustomerNumber}");
        letter.AppendLine().Append("Hiermit kündige ich den oben genannten Vertrag fristgerecht ");
        letter.AppendLine(details.Deadline.HasValue
            ? $"zum nächstmöglichen Zeitpunkt unter Berücksichtigung der Kündigungsfrist (aktuelle Frist: {details.Deadline:dd.MM.yyyy})."
            : "zum nächstmöglichen Zeitpunkt.");
        letter.AppendLine("Bitte bestätigen Sie mir die Kündigung sowie das Vertragsende schriftlich.");

        return Results.Text(letter.ToString(), "text/plain; charset=utf-8");
    }
    private static async Task<IResult> CancellationDeadlines(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        ContractLinkStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(uid, fullWorthSpaceId, ct)) return Results.NotFound();

        var visible = await space.VisibleAccountIdsAsync(uid, fullWorthSpaceId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = (await store.UpcomingDeadlinesAsync(fullWorthSpaceId, ct))
            .Where(row => !row.AccountId.HasValue || visible.Contains(row.AccountId.Value))
            .Select(row => new
            {
                id = row.Id,
                name = row.Name,
                deadline = row.Deadline,
                days = row.Deadline.DayNumber - today.DayNumber,
                status = row.Status
            });
        return Results.Ok(rows);
    }

    private static DateOnly? CalculateDeadline(DateOnly? term,int? value,string? unit){if(!term.HasValue||!value.HasValue)return null;return unit?.ToLowerInvariant() switch{"days"=>term.Value.AddDays(-value.Value),"weeks"=>term.Value.AddDays(-7*value.Value),"months"=>term.Value.AddMonths(-value.Value),_=>null};}
    private static string? NormalizeUnit(string? value)=>value?.Trim().ToLowerInvariant() switch{"days"=>"days","weeks"=>"weeks","months"=>"months",_=>null};
    private static string NormalizeSource(string? value)=>value?.Trim().ToLowerInvariant() switch{"detection"=>"detection","import"=>"import",_=>"manual"};
}
