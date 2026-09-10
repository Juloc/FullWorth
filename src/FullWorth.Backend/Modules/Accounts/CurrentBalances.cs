using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

/// <summary>One account's current balance in one currency.</summary>
public sealed record AccountBalance(
    Guid AccountId,
    decimal Amount,
    string Currency,
    string BalanceType,
    DateOnly? ReferenceDate,
    DateTimeOffset CapturedAt,
    // Where the figure came from (BalanceSources) and the owner's remark. Optional so the many
    // callers that construct a balance for a calculation stay unchanged.
    string? Source = null,
    string? Note = null);

/// <summary>
/// The current balance of an account, PER CURRENCY.
///
/// An account has one declared currency but can hold money in several: PayPal, Wise and Revolut report a
/// wallet per currency, and a sync stores one balance row for each. Every surface used to reduce that to a
/// single row per account — and because a sync stamps every row with the same <c>CapturedAt</c> and those
/// wallet rows often share a balance type, the pick was not even stable: the displayed balance could flip
/// between wallets from one sync to the next, while the money in the other currencies was invisible in the
/// account list, the dashboard, net worth and the history.
///
/// Selection has two independent steps, and mixing them was the bug:
/// 1. per (account, currency): the newest capture, then the balance-type preference below;
/// 2. per account: which of those currencies is shown FIRST (<see cref="Primary"/>) — a display decision
///    that must never decide which money counts.
/// </summary>
public static class CurrentBalances
{
    /// <summary>
    /// Provider balance-type preference. A sync stamps every type with an identical <c>CapturedAt</c>, so
    /// without a tiebreak the chosen balance flips arbitrarily between available and booked.
    /// </summary>
    public static int Rank(string? balanceType) => balanceType switch
    {
        "interimAvailable" => 0,
        "closingAvailable" => 1,
        "closingBooked" => 2,
        "interimBooked" => 3,
        "expected" => 4,
        _ => 5
    };

    /// <summary>Balance types that describe SETTLED money — no pending authorisations included.</summary>
    public static bool IsBooked(string? balanceType) =>
        balanceType is "closingBooked" or "interimBooked";

    public static async Task<List<AccountBalance>> LoadAsync(
        FullWorthDbContext db,
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken ct) =>
        Pick(await LoadRowsAsync(db, accountIds, ct));

    /// <summary>
    /// Every balance row at the newest capture per (account, currency) — before the type preference is
    /// applied. Callers that need more than the one preferred figure (the history back-cast needs the
    /// BOOKED one) use this and pick themselves.
    /// </summary>
    public static async Task<List<AccountBalance>> LoadRowsAsync(
        FullWorthDbContext db,
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken ct)
    {
        if (accountIds.Count == 0) return [];

        // Balance snapshots are history and grow with every sync, so the newest capture per
        // (account, currency) is resolved in SQL first and only those rows are read back.
        var latestCaptures = await db.BalanceSnapshots.AsNoTracking()
            .Where(balance => accountIds.Contains(balance.AccountId))
            .GroupBy(balance => new { balance.AccountId, balance.Currency })
            .Select(group => group.Max(balance => balance.CapturedAt))
            .Distinct()
            .ToListAsync(ct);
        if (latestCaptures.Count == 0) return [];

        var rows = await db.BalanceSnapshots.AsNoTracking()
            .Where(balance =>
                accountIds.Contains(balance.AccountId) &&
                latestCaptures.Contains(balance.CapturedAt))
            .Select(balance => new AccountBalance(
                balance.AccountId, balance.Amount, balance.Currency, balance.BalanceType,
                balance.ReferenceDate, balance.CapturedAt, balance.Source, balance.Note))
            .ToListAsync(ct);

        return rows;
    }

    /// <summary>
    /// Reduces raw rows to one per (account, currency). Kept separate from <see cref="LoadAsync"/> so
    /// callers that already hold the rows use the same rule instead of writing their own ordering.
    /// </summary>
    public static List<AccountBalance> Pick(IEnumerable<AccountBalance> rows) =>
        rows.GroupBy(balance => (balance.AccountId, Currency: Normalize(balance.Currency)))
            .Select(group => group
                .OrderByDescending(balance => balance.CapturedAt)
                .ThenBy(balance => Rank(balance.BalanceType))
                .ThenBy(balance => balance.BalanceType, StringComparer.Ordinal)
                .First())
            .ToList();

    /// <summary>
    /// The settled balance per (account, currency), or nothing for that pair when the provider sent only
    /// an available one. The history back-cast needs this: it walks back over BOOKED transactions, so
    /// anchoring it on a balance that already includes pending authorisations shifts every past day by
    /// the pending amount.
    /// </summary>
    public static List<AccountBalance> PickBooked(IEnumerable<AccountBalance> rows) =>
        Pick(rows.Where(balance => IsBooked(balance.BalanceType)));

    /// <summary>
    /// The balance shown as the account's headline figure: the account's own declared currency when it has
    /// one, then the largest holding. Deterministic, so it cannot change between two syncs of the same
    /// data. This is presentation only — every currency still counts towards every total.
    /// </summary>
    public static AccountBalance? Primary(IEnumerable<AccountBalance> balances, string? accountCurrency)
    {
        var declared = Normalize(accountCurrency);
        return balances
            .OrderByDescending(balance => string.Equals(
                Normalize(balance.Currency), declared, StringComparison.Ordinal))
            .ThenByDescending(balance => balance.Amount)
            .ThenBy(balance => Normalize(balance.Currency), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
}
