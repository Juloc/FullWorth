using System.Text;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Contracts;

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

/// <summary>
/// Kuendigungsfristen: was ein Vertrag an Laufzeit und Frist mitbringt, wann er spaetestens gekuendigt
/// werden muss, und der fertige Brieftext dazu.
/// </summary>
public static class ContractCancellationEndpoints
{
    public static IEndpointRouteBuilder MapContractCancellationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts").WithTags("Contracts");
        group.MapGet("/cancellations", ListCancellations);
        group.MapGet("/cancellation-deadlines", CancellationDeadlines);
        group.MapGet("/{contractId:guid}/cancellation", GetCancellation);
        group.MapPut("/{contractId:guid}/cancellation", PutCancellation);
        group.MapGet("/{contractId:guid}/cancellation-letter", CancellationLetter);
        return app;
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
}
