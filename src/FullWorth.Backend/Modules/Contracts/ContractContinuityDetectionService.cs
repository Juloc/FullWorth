using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Transactions;
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

/// <summary>
/// Conservative read-only detector for duplicate contract rows that are likely one logical contract
/// continuing on a new payment account. It never merges or mutates finance data.
/// </summary>
public sealed class ContractContinuityDetectionService(FullWorthDbContext db)
{
    public async Task<IReadOnlyList<ContractContinuityCandidate>> DetectForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        var member = await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
            x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (!member) return [];

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
        if (contracts.Count < 2) return [];

        var accountIds = contracts.Select(x => x.AccountId!.Value).Distinct().ToArray();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-450));
        var transactions = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                accountIds.Contains(transaction.AccountId) &&
                transaction.Amount < 0m &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.BookingDate != null &&
                transaction.BookingDate >= from)
            .Select(transaction => new ContractContinuityPayment(
                transaction.AccountId,
                transaction.BookingDate!.Value,
                -transaction.Amount,
                transaction.Currency,
                transaction.Counterparty,
                transaction.NormalizedCounterparty))
            .ToListAsync(ct);

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
                    if (left.AccountId == right.AccountId) continue;
                    if (!AmountsCompatible(left.Amount, right.Amount)) continue;

                    var leftPayments = PaymentsFor(transactions, group.Key.Identity, left.AccountId!.Value, group.Key.Currency);
                    var rightPayments = PaymentsFor(transactions, group.Key.Identity, right.AccountId!.Value, group.Key.Currency);
                    if (leftPayments.Count < 1 || rightPayments.Count < 1) continue;

                    var leftFirst = leftPayments[0].Date;
                    var rightFirst = rightPayments[0].Date;
                    var older = leftFirst <= rightFirst ? left : right;
                    var newer = ReferenceEquals(older, left) ? right : left;
                    var olderPayments = ReferenceEquals(older, left) ? leftPayments : rightPayments;
                    var newerPayments = ReferenceEquals(newer, right) ? rightPayments : leftPayments;

                    // Require a meaningful old history and at least one payment on the new account.
                    if (olderPayments.Count < 2 || newerPayments.Count < 1) continue;

                    var olderLast = olderPayments[^1].Date;
                    var newerFirst = newerPayments[0].Date;
                    if (newerFirst <= olderLast) continue; // overlapping histories are likely separate contracts

                    var expected = ContractCycle.Next(
                        olderLast,
                        group.Key.Cycle,
                        group.Key.Interval);
                    var deviationDays = Math.Abs(newerFirst.DayNumber - expected.DayNumber);
                    if (deviationDays > ContinuityToleranceDays(group.Key.Cycle, group.Key.Interval)) continue;

                    var amountDeltaRatio = RelativeDifference(older.Amount, newer.Amount);
                    var timingScore = 1m - Math.Min(1m, deviationDays / (decimal)Math.Max(1, ContinuityToleranceDays(group.Key.Cycle, group.Key.Interval)));
                    var amountScore = 1m - Math.Min(1m, amountDeltaRatio / .20m);
                    var confidence = Math.Round(
                        Math.Clamp(.72m + timingScore * .16m + amountScore * .12m, 0m, 1m),
                        3,
                        MidpointRounding.AwayFromZero);

                    result.Add(new ContractContinuityCandidate(
                        older.Id,
                        newer.Id,
                        group.Key.Identity,
                        older.AccountId!.Value,
                        newer.AccountId!.Value,
                        olderLast,
                        newerFirst,
                        older.Amount,
                        newer.Amount,
                        group.Key.Currency,
                        group.Key.Cycle,
                        group.Key.Interval,
                        confidence));
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

    private static List<ContractContinuityPayment> PaymentsFor(
        IReadOnlyList<ContractContinuityPayment> transactions,
        string identity,
        Guid accountId,
        string currency) =>
        transactions
            .Where(x =>
                x.AccountId == accountId &&
                string.Equals(x.Currency, currency, StringComparison.OrdinalIgnoreCase) &&
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

    private sealed record ContractContinuityPayment(
        Guid AccountId,
        DateOnly Date,
        decimal Amount,
        string Currency,
        string? Counterparty,
        string? NormalizedCounterparty);
}
