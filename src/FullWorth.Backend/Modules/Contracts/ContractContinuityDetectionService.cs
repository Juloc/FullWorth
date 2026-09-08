using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Contracts;

public sealed record ContractContinuityCandidate(
    Guid OlderContractId,
    Guid NewerContractId,
    string Provider,
    Guid OlderAccountId,
    Guid NewerAccountId,
    DateOnly OlderLastPayment,
    DateOnly NewerFirstPayment,
    decimal OlderAmount,
    decimal NewerAmount,
    string Currency,
    string BillingCycle,
    int Interval,
    decimal Confidence);

public sealed record ContractPaymentAccountChangeCandidate(
    Guid ContractId,
    string Provider,
    Guid OlderAccountId,
    Guid NewerAccountId,
    DateOnly OlderLastPayment,
    DateOnly NewerFirstPayment,
    decimal OlderAmount,
    decimal NewerAmount,
    string Currency,
    string BillingCycle,
    int Interval,
    decimal Confidence);

public sealed record ContractRelationshipDetectionResult(
    IReadOnlyList<ContractContinuityCandidate> Continuity,
    IReadOnlyList<ContractPaymentAccountChangeCandidate> AccountChanges);

/// <summary>
/// Conservative read-only relationship detector for contract rows and payment-account changes.
/// A relationship is only emitted when the old payment history ends before the new history begins
/// and the first new payment lands near the next expected cycle. It never mutates finance data.
/// </summary>
public sealed class ContractContinuityDetectionService(FullWorthDbContext db)
{
    public async Task<IReadOnlyList<ContractContinuityCandidate>> DetectForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        var state = await LoadStateAsync(userId, fullWorthSpaceId, [], ct);
        return state is null ? [] : DetectContinuity(state.Contracts, state.Payments);
    }

    public async Task<ContractRelationshipDetectionResult> DetectRelationshipsForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        IReadOnlyList<ContractCandidate> recurrenceCandidates,
        CancellationToken ct)
    {
        var candidateAccountIds = recurrenceCandidates
            .Where(candidate => candidate.AccountId.HasValue)
            .Select(candidate => candidate.AccountId!.Value)
            .Distinct()
            .ToArray();
        var state = await LoadStateAsync(userId, fullWorthSpaceId, candidateAccountIds, ct);
        if (state is null) return new([], []);

        return new(
            DetectContinuity(state.Contracts, state.Payments),
            DetectAccountChanges(state.Contracts, state.Payments, recurrenceCandidates));
    }

    private async Task<ContractRelationshipState?> LoadStateAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        IReadOnlyCollection<Guid> additionalAccountIds,
        CancellationToken ct)
    {
        var member = await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
            x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (!member) return null;

        var contracts = await db.Contracts.AsNoTracking()
            .Where(contract =>
                contract.FullWorthSpaceId == fullWorthSpaceId &&
                contract.MergedIntoContractId == null &&
                contract.IsActive &&
                contract.AccountId != null &&
                db.AccountOwners.Any(owner =>
                    owner.AccountId == contract.AccountId.Value &&
                    owner.UserId == userId))
            .ToListAsync(ct);

        if (contracts.Count == 0)
            return new([], []);

        var accountIds = contracts.Select(x => x.AccountId!.Value).ToHashSet();
        if (additionalAccountIds.Count > 0)
        {
            var visibleAdditional = await db.AccountOwners.AsNoTracking()
                .Where(owner => owner.UserId == userId && additionalAccountIds.Contains(owner.AccountId))
                .Join(
                    db.Accounts.AsNoTracking().Where(account => account.FullWorthSpaceId == fullWorthSpaceId),
                    owner => owner.AccountId,
                    account => account.Id,
                    (_, account) => account.Id)
                .Distinct()
                .ToListAsync(ct);
            accountIds.UnionWith(visibleAdditional);
        }

        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-450));
        var payments = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                accountIds.Contains(transaction.AccountId) &&
                transaction.Amount < 0m &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.BookingDate != null &&
                transaction.BookingDate >= from)
            .Select(transaction => new ContractRelationshipPayment(
                transaction.AccountId,
                transaction.BookingDate!.Value,
                -transaction.Amount,
                transaction.Currency,
                transaction.Counterparty,
                transaction.NormalizedCounterparty))
            .ToListAsync(ct);

        return new(contracts, payments);
    }

    private static IReadOnlyList<ContractContinuityCandidate> DetectContinuity(
        IReadOnlyList<RecurringContract> contracts,
        IReadOnlyList<ContractRelationshipPayment> payments)
    {
        var result = new List<ContractContinuityCandidate>();
        foreach (var group in contracts
                     .Select(contract => new
                     {
                         Contract = contract,
                         Identity = ContractIdentity.Normalize(contract.ProviderName ?? contract.Name),
                         Cycle = NormalizeCycle(contract.BillingCycle)
                     })
                     .Where(x => x.Identity.Length >= 4)
                     .GroupBy(x => new
                     {
                         x.Identity,
                         Currency = x.Contract.Currency.Trim().ToUpperInvariant(),
                         x.Cycle,
                         Interval = Math.Max(1, x.Contract.Interval)
                     }))
        {
            var rows = group.Select(x => x.Contract).OrderBy(x => x.CreatedAt).ToList();
            if (rows.Count < 2) continue;

            for (var i = 0; i < rows.Count; i++)
            {
                for (var j = i + 1; j < rows.Count; j++)
                {
                    var left = rows[i];
                    var right = rows[j];
                    if (left.AccountId == right.AccountId || !AmountsCompatible(left.Amount, right.Amount)) continue;

                    var leftPayments = PaymentsFor(
                        payments, group.Key.Identity, left.AccountId!.Value, group.Key.Currency, left.Amount);
                    var rightPayments = PaymentsFor(
                        payments, group.Key.Identity, right.AccountId!.Value, group.Key.Currency, right.Amount);
                    if (!TryContinuity(
                            leftPayments,
                            rightPayments,
                            group.Key.Cycle,
                            group.Key.Interval,
                            left.Amount,
                            right.Amount,
                            out var continuity))
                        continue;

                    var older = continuity.LeftIsOlder ? left : right;
                    var newer = continuity.LeftIsOlder ? right : left;
                    result.Add(new ContractContinuityCandidate(
                        older.Id,
                        newer.Id,
                        group.Key.Identity,
                        older.AccountId!.Value,
                        newer.AccountId!.Value,
                        continuity.OlderLast,
                        continuity.NewerFirst,
                        older.Amount,
                        newer.Amount,
                        group.Key.Currency,
                        group.Key.Cycle,
                        group.Key.Interval,
                        continuity.Confidence));
                }
            }
        }

        return result
            .GroupBy(x => PairKey(x.OlderContractId, x.NewerContractId))
            .Select(group => group.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.NewerFirstPayment)
            .Take(20)
            .ToList();
    }

    private static IReadOnlyList<ContractPaymentAccountChangeCandidate> DetectAccountChanges(
        IReadOnlyList<RecurringContract> contracts,
        IReadOnlyList<ContractRelationshipPayment> payments,
        IReadOnlyList<ContractCandidate> recurrenceCandidates)
    {
        if (contracts.Count == 0 || recurrenceCandidates.Count == 0) return [];

        var result = new List<ContractPaymentAccountChangeCandidate>();
        foreach (var candidate in recurrenceCandidates
                     .Where(x => x.AccountId.HasValue)
                     .OrderByDescending(x => x.Confidence)
                     .ThenByDescending(x => x.LastPaymentDate))
        {
            var identity = ContractIdentity.Normalize(candidate.Counterparty);
            if (identity.Length < 4) continue;
            var currency = candidate.Currency.Trim().ToUpperInvariant();
            var cycle = NormalizeCycle(candidate.BillingCycle);
            var interval = Math.Max(1, candidate.Interval);
            var newAccountId = candidate.AccountId!.Value;

            // A root contract already on the newly observed account means this is a duplicate-row
            // relationship, not an account-change proposal.
            if (contracts.Any(contract =>
                    contract.AccountId == newAccountId &&
                    string.Equals(contract.Currency, currency, StringComparison.OrdinalIgnoreCase) &&
                    ContractIdentity.Normalize(contract.ProviderName ?? contract.Name) == identity))
                continue;

            foreach (var contract in contracts
                         .Where(contract =>
                             contract.AccountId.HasValue &&
                             contract.AccountId.Value != newAccountId &&
                             string.Equals(contract.Currency, currency, StringComparison.OrdinalIgnoreCase) &&
                             ContractIdentity.Normalize(contract.ProviderName ?? contract.Name) == identity &&
                             NormalizeCycle(contract.BillingCycle) == cycle &&
                             Math.Max(1, contract.Interval) == interval &&
                             AmountsCompatible(contract.Amount, candidate.TypicalAmount))
                         .OrderBy(contract => contract.CreatedAt))
            {
                var oldPayments = PaymentsFor(
                    payments, identity, contract.AccountId!.Value, currency, contract.Amount);
                var newPayments = PaymentsFor(
                    payments, identity, newAccountId, currency, candidate.TypicalAmount);
                if (!TryOrderedContinuity(
                        oldPayments,
                        newPayments,
                        cycle,
                        interval,
                        contract.Amount,
                        candidate.TypicalAmount,
                        out var continuity))
                    continue;

                // The recurrence detector's newest evidence must actually be on the proposed new
                // account; stale candidates from a previous account must not move a contract forward.
                if (newPayments[^1].Date != candidate.LastPaymentDate) continue;

                result.Add(new ContractPaymentAccountChangeCandidate(
                    contract.Id,
                    identity,
                    contract.AccountId.Value,
                    newAccountId,
                    continuity.OlderLast,
                    continuity.NewerFirst,
                    contract.Amount,
                    candidate.TypicalAmount,
                    currency,
                    cycle,
                    interval,
                    Math.Min(candidate.Confidence, continuity.Confidence)));
                break;
            }
        }

        return result
            .GroupBy(x => $"{x.ContractId:N}:{x.NewerAccountId:N}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(x => x.Confidence)
            .Take(20)
            .ToList();
    }

    private static bool TryContinuity(
        IReadOnlyList<ContractRelationshipPayment> left,
        IReadOnlyList<ContractRelationshipPayment> right,
        string cycle,
        int interval,
        decimal leftAmount,
        decimal rightAmount,
        out ContinuityEvidence evidence)
    {
        evidence = default!;
        if (left.Count < 2 || right.Count < 1) return false;

        var leftFirst = left[0].Date;
        var rightFirst = right[0].Date;
        if (leftFirst <= rightFirst)
            return TryOrderedContinuity(left, right, cycle, interval, leftAmount, rightAmount, out evidence);

        if (!TryOrderedContinuity(right, left, cycle, interval, rightAmount, leftAmount, out var reversed))
            return false;
        evidence = reversed with { LeftIsOlder = false };
        return true;
    }

    private static bool TryOrderedContinuity(
        IReadOnlyList<ContractRelationshipPayment> olderPayments,
        IReadOnlyList<ContractRelationshipPayment> newerPayments,
        string cycle,
        int interval,
        decimal olderAmount,
        decimal newerAmount,
        out ContinuityEvidence evidence)
    {
        evidence = default!;
        if (olderPayments.Count < 2 || newerPayments.Count < 1) return false;

        var olderLast = olderPayments[^1].Date;
        var newerFirst = newerPayments[0].Date;
        if (newerFirst <= olderLast) return false;

        var expected = ContractCycle.Next(olderLast, cycle, interval);
        var tolerance = ContinuityToleranceDays(cycle, interval);
        var deviationDays = Math.Abs(newerFirst.DayNumber - expected.DayNumber);
        if (deviationDays > tolerance) return false;

        var amountDeltaRatio = RelativeDifference(olderAmount, newerAmount);
        var timingScore = 1m - Math.Min(1m, deviationDays / (decimal)Math.Max(1, tolerance));
        var amountScore = 1m - Math.Min(1m, amountDeltaRatio / .20m);
        var confidence = Math.Round(
            Math.Clamp(.72m + timingScore * .16m + amountScore * .12m, 0m, 1m),
            3,
            MidpointRounding.AwayFromZero);

        evidence = new ContinuityEvidence(true, olderLast, newerFirst, confidence);
        return true;
    }

    private static List<ContractRelationshipPayment> PaymentsFor(
        IReadOnlyList<ContractRelationshipPayment> transactions,
        string identity,
        Guid accountId,
        string currency,
        decimal expectedAmount) =>
        transactions
            .Where(x =>
                x.AccountId == accountId &&
                string.Equals(x.Currency, currency, StringComparison.OrdinalIgnoreCase) &&
                AmountsCompatible(x.Amount, expectedAmount) &&
                (ContractIdentity.Matches(identity, x.NormalizedCounterparty) ||
                 ContractIdentity.Matches(identity, x.Counterparty)))
            .OrderBy(x => x.Date)
            .ToList();

    private static bool AmountsCompatible(decimal left, decimal right)
    {
        var a = Math.Abs(left);
        var b = Math.Abs(right);
        if (a <= 0m || b <= 0m) return false;
        return RelativeDifference(a, b) <= .20m;
    }

    private static decimal RelativeDifference(decimal left, decimal right)
    {
        var a = Math.Abs(left);
        var b = Math.Abs(right);
        var baseline = Math.Max(a, b);
        return baseline == 0m ? 0m : Math.Abs(a - b) / baseline;
    }

    private static int ContinuityToleranceDays(string cycle, int interval) => cycle switch
    {
        "daily" => Math.Max(2, interval * 2),
        "weekly" => Math.Max(5, interval * 4),
        "quarterly" => 35,
        "yearly" => 60,
        _ => 20
    };

    private static string NormalizeCycle(string? cycle) =>
        string.IsNullOrWhiteSpace(cycle) ? "monthly" : cycle.Trim().ToLowerInvariant();

    private static string PairKey(Guid first, Guid second) =>
        first.CompareTo(second) <= 0 ? $"{first:N}:{second:N}" : $"{second:N}:{first:N}";

    private sealed record ContractRelationshipState(
        IReadOnlyList<RecurringContract> Contracts,
        IReadOnlyList<ContractRelationshipPayment> Payments);

    private sealed record ContractRelationshipPayment(
        Guid AccountId,
        DateOnly Date,
        decimal Amount,
        string Currency,
        string? Counterparty,
        string? NormalizedCounterparty);

    private sealed record ContinuityEvidence(
        bool LeftIsOlder,
        DateOnly OlderLast,
        DateOnly NewerFirst,
        decimal Confidence);
}
