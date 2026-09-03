using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

public enum TransferDetectionResult { Success, NotFound, Forbidden }
public sealed record TransferDetectionSummary(int Evaluated, int PairsLinked, bool Applied);
public sealed record TransferDetectionOutcome(TransferDetectionResult Result, TransferDetectionSummary? Summary = null);

public sealed record TransferCandidate(Guid Id, Guid AccountId, decimal Amount, string Currency, DateOnly? BookingDate);
public sealed record TransferCandidateLeg(Guid Id, Guid AccountId, string Account, decimal Amount, string Currency, DateOnly? BookingDate, string? Counterparty);
public sealed record TransferCandidatePair(TransferCandidateLeg First, TransferCandidateLeg Second);
public sealed record TransferCandidatesOutcome(TransferDetectionResult Result, List<TransferCandidatePair>? Pairs);

/// <summary>
/// Conservative, deterministic detection of internal transfers: a debit on one account paired with
/// an equal-and-opposite credit on another account of the same FullWorth Space within a short date
/// window. Only mutually-unique pairs are linked; anything ambiguous is left for manual review.
/// </summary>
public sealed class TransferDetectionService(FullWorthDbContext db)
{
    private const int WindowDays = 3;

    public async Task<TransferDetectionOutcome> DetectForUserAsync(Guid userId, Guid fullWorthSpaceId, bool apply, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => x.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return new(TransferDetectionResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(TransferDetectionResult.Forbidden);

        var candidates = await db.Transactions.AsNoTracking()
            .Where(t => t.TransferGroupId == null && !t.IsIgnored && t.Amount != 0m && t.BookingDate != null &&
                        db.Accounts.Any(a => a.Id == t.AccountId && a.FullWorthSpaceId == fullWorthSpaceId))
            .Select(t => new TransferCandidate(t.Id, t.AccountId, t.Amount, t.Currency, t.BookingDate))
            .ToListAsync(ct);

        var pairs = FindMutualUniquePairs(candidates, WindowDays);

        if (apply && pairs.Count > 0)
        {
            var ids = pairs.SelectMany(p => new[] { p.First, p.Second }).ToHashSet();
            var tracked = await db.Transactions.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            foreach (var (first, second) in pairs)
            {
                var groupId = Guid.NewGuid();
                foreach (var id in new[] { first, second })
                {
                    var tx = tracked[id];
                    tx.TransferGroupId = groupId;
                    tx.IsTransfer = true;
                    tx.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        return new(TransferDetectionResult.Success, new TransferDetectionSummary(candidates.Count, pairs.Count, apply));
    }

    /// <summary>
    /// Detected candidate pairs with enough detail to render a review list, WITHOUT linking anything
    /// (§9.7 Flow D: the user confirms or rejects each suggestion before it becomes a real link).
    /// </summary>
    public async Task<TransferCandidatesOutcome> CandidatesForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => x.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return new(TransferDetectionResult.NotFound, null);
        if (role != FullWorthSpaceRoles.Owner) return new(TransferDetectionResult.Forbidden, null);

        var candidates = await db.Transactions.AsNoTracking()
            .Where(t => t.TransferGroupId == null && !t.IsIgnored && t.Amount != 0m && t.BookingDate != null &&
                        db.Accounts.Any(a => a.Id == t.AccountId && a.FullWorthSpaceId == fullWorthSpaceId))
            .Select(t => new TransferCandidate(t.Id, t.AccountId, t.Amount, t.Currency, t.BookingDate))
            .ToListAsync(ct);
        var pairs = FindMutualUniquePairs(candidates, WindowDays);
        if (pairs.Count == 0) return new(TransferDetectionResult.Success, []);

        var ids = pairs.SelectMany(p => new[] { p.First, p.Second }).ToHashSet();
        var details = await db.Transactions.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .Select(t => new TransferCandidateLeg(t.Id, t.AccountId, db.Accounts.Where(a => a.Id == t.AccountId).Select(a => a.DisplayName).FirstOrDefault() ?? "", t.Amount, t.Currency, t.BookingDate, t.Counterparty))
            .ToDictionaryAsync(t => t.Id, ct);
        var result = pairs.Select(p => new TransferCandidatePair(details[p.First], details[p.Second])).ToList();
        return new(TransferDetectionResult.Success, result);
    }

    public static List<(Guid First, Guid Second)> FindMutualUniquePairs(IReadOnlyList<TransferCandidate> candidates, int windowDays)
    {
        var ordered = candidates
            .OrderBy(c => Math.Abs(c.Amount)).ThenBy(c => c.BookingDate).ThenBy(c => c.Id)
            .ToList();

        var counterparts = ordered.ToDictionary(c => c.Id, _ => new List<Guid>());
        for (var i = 0; i < ordered.Count; i++)
            for (var j = i + 1; j < ordered.Count; j++)
                if (IsCounterpart(ordered[i], ordered[j], windowDays))
                {
                    counterparts[ordered[i].Id].Add(ordered[j].Id);
                    counterparts[ordered[j].Id].Add(ordered[i].Id);
                }

        var pairs = new List<(Guid, Guid)>();
        var used = new HashSet<Guid>();
        foreach (var candidate in ordered)
        {
            if (used.Contains(candidate.Id)) continue;
            var options = counterparts[candidate.Id].Where(id => !used.Contains(id)).ToList();
            if (options.Count != 1) continue;               // no or ambiguous counterpart -> skip
            var other = options[0];
            var otherOptions = counterparts[other].Where(id => !used.Contains(id)).ToList();
            if (otherOptions.Count != 1) continue;          // not mutually unique -> skip
            pairs.Add((candidate.Id, other));
            used.Add(candidate.Id);
            used.Add(other);
        }
        return pairs;
    }

    private static bool IsCounterpart(TransferCandidate a, TransferCandidate b, int windowDays) =>
        a.AccountId != b.AccountId &&
        string.Equals(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase) &&
        a.Amount == -b.Amount &&
        a.BookingDate.HasValue && b.BookingDate.HasValue &&
        Math.Abs(a.BookingDate.Value.DayNumber - b.BookingDate.Value.DayNumber) <= windowDays;
}

public static class TransferEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/transfers/detect", async (
            Guid fullWorthSpaceId,
            bool? apply,
            CurrentUserContext currentUser,
            TransferDetectionService service,
            CancellationToken ct) =>
        {
            var outcome = await service.DetectForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, apply ?? false, ct);
            return outcome.Result switch
            {
                TransferDetectionResult.Success => Results.Ok(outcome.Summary),
                TransferDetectionResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        }).WithTags("Transfers");

        // §9.7 Flow D: candidates for the user to confirm/reject, before anything is linked.
        app.MapGet("/api/transfers/candidates", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            TransferDetectionService service,
            CancellationToken ct) =>
        {
            var outcome = await service.CandidatesForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return outcome.Result switch
            {
                TransferDetectionResult.Success => Results.Ok(outcome.Pairs),
                TransferDetectionResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        }).WithTags("Transfers");
        return app;
    }
}
