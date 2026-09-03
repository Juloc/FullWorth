using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using FullWorth.Backend.Validation;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Contracts.Review;

/// <summary>
/// A recurring-contract detection candidate the owner has explicitly rejected, so it is suppressed
/// from future detection results. Identity is the candidate's normalized counterparty + currency
/// (the same key the detector groups on), kept as an explicit, auditable row.
/// </summary>
public sealed class DismissedContractCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Counterparty { get; set; } = string.Empty;
    public string Currency { get; set; } = "EUR";
    public DateTimeOffset DismissedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record CandidateDismissal(string Counterparty, string Currency);

/// <summary>
/// Owner-gated review actions for detected recurring-contract candidates. Confirming a candidate is
/// <see cref="ContractDetectionService.AcceptForUserAsync"/> (creates the contract); this store adds
/// the reject/dismiss side so a candidate the owner does not want stops reappearing in detection.
/// </summary>
public sealed class ContractCandidateReviewStore(
    FullWorthDbContext db,
    IntelligenceFeedbackRecorder intelligenceFeedback)
{
    public async Task<ContractMutationResult> DismissForUserAsync(Guid userId, Guid fullWorthSpaceId, CandidateDismissal request, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return ContractMutationResult.NotFound;
        if (role != FullWorthSpaceRoles.Owner) return ContractMutationResult.Forbidden;

        var counterparty = request.Counterparty?.Trim();
        if (string.IsNullOrWhiteSpace(counterparty)) return ContractMutationResult.Invalid;
        if (!Validate.IsCurrency(request.Currency)) return ContractMutationResult.Invalid;
        var currency = request.Currency.Trim().ToUpperInvariant();

        var alreadyDismissed = await db.DismissedContractCandidates.AnyAsync(dismissed =>
            dismissed.FullWorthSpaceId == fullWorthSpaceId &&
            dismissed.Counterparty == counterparty &&
            dismissed.Currency == currency, ct);
        if (!alreadyDismissed)
        {
            var row = new DismissedContractCandidate
            {
                FullWorthSpaceId = fullWorthSpaceId,
                Counterparty = counterparty,
                Currency = currency,
            };
            db.DismissedContractCandidates.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                await intelligenceFeedback.RecordContractDecisionAsync(
                    fullWorthSpaceId,
                    userId,
                    counterparty,
                    currency,
                    accepted: false,
                    contractId: null,
                    billingCycle: null,
                    interval: null,
                    ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent request inserted the same candidate first. The financial action is
                // idempotent; only the winning insert records the feedback event.
                db.Entry(row).State = EntityState.Detached;
            }
        }

        return ContractMutationResult.Success;
    }
}

public static class ContractCandidateReviewEndpoints
{
    public static IEndpointRouteBuilder MapContractCandidateReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/contracts/detection/dismiss", async (
            Guid fullWorthSpaceId, CandidateDismissal request,
            CurrentUserContext currentUser, ContractCandidateReviewStore store, CancellationToken ct) =>
        {
            var result = await store.DismissForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
            return result switch
            {
                ContractMutationResult.Success => Results.NoContent(),
                ContractMutationResult.NotFound => Results.NotFound(),
                ContractMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                ContractMutationResult.Invalid => Results.Problem(
                    detail: "A counterparty and a three-letter currency are required.",
                    statusCode: StatusCodes.Status400BadRequest),
                _ => Results.StatusCode(StatusCodes.Status409Conflict),
            };
        }).WithTags("Contracts");

        return app;
    }
}
