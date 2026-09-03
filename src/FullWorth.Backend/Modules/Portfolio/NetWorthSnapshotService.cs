using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Parity;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class NetWorthSnapshotService(
    FullWorthDbContext db,
    InvestmentNetWorthService investments,
    WealthOverviewService? wealth = null)
{
    public async Task<List<NetWorthSnapshot>> CaptureTodayAsync(CancellationToken ct)
    {
        var audiences = await db.FullWorthSpaceMembers.AsNoTracking()
            .Select(x => new { x.FullWorthSpaceId, x.UserId })
            .Distinct()
            .ToListAsync(ct);

        var result = new List<NetWorthSnapshot>();
        foreach (var audience in audiences)
            result.AddRange(await CaptureForUserAsync(audience.FullWorthSpaceId, audience.UserId, ct));
        return result;
    }

    /// <summary>
    /// Full repair pass used once when the worker starts. This makes already-imported historical data
    /// visible without requiring the user to re-import it after upgrading to the consistency pipeline.
    /// </summary>
    public async Task<List<NetWorthSnapshot>> RebuildAllHistoryAsync(CancellationToken ct)
    {
        var spaces = await db.FullWorthSpaceMembers.AsNoTracking()
            .Select(member => member.FullWorthSpaceId)
            .Distinct()
            .ToListAsync(ct);
        var result = new List<NetWorthSnapshot>();
        foreach (var fullWorthSpaceId in spaces)
            result.AddRange(await RebuildHistoryForSpaceAsync(fullWorthSpaceId, null, ct));
        return result;
    }

    public async Task<List<NetWorthSnapshot>> RebuildHistoryForSpaceAsync(Guid fullWorthSpaceId, DateOnly? from, CancellationToken ct)
    {
        var users = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId)
            .Select(member => member.UserId)
            .Distinct()
            .ToListAsync(ct);
        var result = new List<NetWorthSnapshot>();
        foreach (var userId in users)
            result.AddRange(await RebuildHistoryForUserAsync(fullWorthSpaceId, userId, from, ct));
        return result;
    }

    public Task<List<NetWorthSnapshot>> CaptureForUserAsync(Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return RebuildHistoryForUserAsync(fullWorthSpaceId, userId, today, ct);
    }

    /// <summary>
    /// Rebuilds materialized net-worth history from source data. The latest bank balance is the anchor;
    /// booked transactions are then walked backwards, so an imported transaction from two years ago
    /// creates a real historical balance point instead of only changing today's analytics.
    ///
    /// Asset/liability history cannot be reconstructed from a single current value. Existing historical
    /// snapshot components are therefore preserved and carried forward across gaps; dates before the first
    /// recorded portfolio snapshot remain zero instead of copying today's value into the past.
    /// Investment portfolios are valued for today's source-of-truth snapshot and their linked bank accounts
    /// are excluded from the bank component to prevent double counting. Older investment components remain
    /// whatever was actually materialized in historical snapshots rather than inventing current values in the past.
    /// Loans are included in today's legacy liability component from their canonical Loan balance. The V2 wealth
    /// decomposition is materialized separately after this compatibility history pass and is never fabricated for
    /// old dates that predate explicit component storage.
    /// </summary>
    public async Task<List<NetWorthSnapshot>> RebuildHistoryForUserAsync(
        Guid fullWorthSpaceId,
        Guid userId,
        DateOnly? from,
        CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(
                x => x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct))
            return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var investment = await investments.CalculateAsync(fullWorthSpaceId, userId, today, ct);
        var excludedInvestmentAccounts = investment.ExcludedLinkedAccountIds;

        var accounts = await db.Accounts.AsNoTracking()
            .Where(account =>
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.IsActive &&
                account.IncludeInNetWorth &&
                account.Owners.Any(owner => owner.UserId == userId) &&
                !excludedInvestmentAccounts.Contains(account.Id))
            .Select(account => new HistoricalAccount(account.Id, account.Currency))
            .ToListAsync(ct);
        var accountIds = accounts.Select(account => account.Id).ToArray();
        var accountCurrency = accounts.ToDictionary(account => account.Id, account => account.Currency, EqualityComparer<Guid>.Default);

        // An empty accountIds array is translated to a false predicate by EF, so no special empty-list
        // branch is needed. Keeping one query shape also avoids target-typing problems with anonymous rows.
        var transactionRows = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                accountIds.Contains(transaction.AccountId) &&
                transaction.Status != "PDNG" &&
                (transaction.BookingDate != null || transaction.ValueDate != null))
            .Select(transaction => new
            {
                transaction.AccountId,
                transaction.BookingDate,
                transaction.ValueDate,
                transaction.Amount,
                transaction.Currency
            })
            .ToListAsync(ct);
        var transactions = transactionRows
            .Select(row => new HistoricalTransaction(
                row.AccountId,
                row.BookingDate ?? row.ValueDate!.Value,
                row.Amount,
                row.Currency))
            .Where(row => row.Date <= today)
            .ToList();

        var existingQuery = db.NetWorthSnapshots
            .Where(snapshot => snapshot.FullWorthSpaceId == fullWorthSpaceId && snapshot.UserId == userId);
        if (from.HasValue) existingQuery = existingQuery.Where(snapshot => snapshot.Date >= from.Value);
        var existing = await existingQuery.OrderBy(snapshot => snapshot.Date).ToListAsync(ct);

        var start = from;
        if (!start.HasValue)
        {
            var earliestTransaction = transactions.Count == 0 ? (DateOnly?)null : transactions.Min(row => row.Date);
            var earliestExisting = existing.Count == 0 ? (DateOnly?)null : existing.Min(snapshot => snapshot.Date);
            start = Min(earliestTransaction, earliestExisting) ?? today;
        }
        if (start.Value > today) start = today;

        // If a bounded refresh was requested, rows before the bound are not needed for the backward walk.
        transactions = transactions.Where(row => row.Date >= start.Value).ToList();

        var balanceRows = await db.BalanceSnapshots.AsNoTracking()
            .Where(balance => accountIds.Contains(balance.AccountId))
            .ToListAsync(ct);
        var latestBalances = balanceRows
            .GroupBy(balance => balance.AccountId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(balance => balance.CapturedAt)
                    .ThenBy(balance => BalanceRank(balance.BalanceType))
                    .ThenBy(balance => balance.BalanceType, StringComparer.Ordinal)
                    .First());

        var assetRows = await db.Assets.AsNoTracking()
            .Where(asset => asset.FullWorthSpaceId == fullWorthSpaceId && asset.IncludeInNetWorth)
            .Select(asset => new { asset.CurrentValue, asset.Currency })
            .ToListAsync(ct);
        var liabilityRows = await db.Liabilities.AsNoTracking()
            .Where(liability => liability.FullWorthSpaceId == fullWorthSpaceId && liability.IncludeInNetWorth)
            .Select(liability => new { liability.CurrentBalance, liability.Currency })
            .ToListAsync(ct);
        var loanRows = await db.Loans.AsNoTracking()
            .Where(loan => loan.FullWorthSpaceId == fullWorthSpaceId && loan.IsActive)
            .Select(loan => new { loan.CurrentBalance, loan.Currency })
            .ToListAsync(ct);

        var currentAssets = assetRows.GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.CurrentValue), StringComparer.OrdinalIgnoreCase);
        var currentLiabilities = liabilityRows.GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.CurrentBalance), StringComparer.OrdinalIgnoreCase);
        foreach (var group in loanRows.GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase))
            currentLiabilities[group.Key] = currentLiabilities.GetValueOrDefault(group.Key) + group.Sum(row => row.CurrentBalance);

        // InvestmentNetWorthService already converts every portfolio to the FullWorth Space base currency.
        // Add it to today's asset component and keep the key even for a zero-valued linked portfolio so
        // excluding the linked bank account cannot make the base-currency snapshot disappear.
        if (investment.Amount != 0m || excludedInvestmentAccounts.Count > 0)
            currentAssets[investment.BaseCurrency] = currentAssets.GetValueOrDefault(investment.BaseCurrency) + investment.Amount;

        var currencies = existing.Select(snapshot => snapshot.Currency)
            .Concat(accounts.Select(account => account.Currency))
            .Concat(currentAssets.Keys)
            .Concat(currentLiabilities.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currencies.Count == 0) return [];

        var existingByKey = existing.ToDictionary(
            snapshot => (snapshot.Date, Currency: snapshot.Currency.ToUpperInvariant()),
            snapshot => snapshot);
        var result = new List<NetWorthSnapshot>();

        foreach (var currency in currencies)
        {
            var normalizedCurrency = currency.ToUpperInvariant();
            var currentAccounts = accounts
                .Where(account => string.Equals(account.Currency, normalizedCurrency, StringComparison.OrdinalIgnoreCase))
                .Sum(account => latestBalances.TryGetValue(account.Id, out var balance) ? balance.Amount : 0m);

            var dailyDelta = transactions
                .Where(transaction =>
                    accountCurrency.TryGetValue(transaction.AccountId, out var nativeCurrency) &&
                    string.Equals(nativeCurrency, normalizedCurrency, StringComparison.OrdinalIgnoreCase) &&
                    // Back-casting an account balance is only valid in the account's native currency.
                    // Foreign booking amounts are intentionally not mixed into the native balance.
                    string.Equals(transaction.Currency, nativeCurrency, StringComparison.OrdinalIgnoreCase))
                .GroupBy(transaction => transaction.Date)
                .ToDictionary(group => group.Key, group => group.Sum(transaction => transaction.Amount));

            var components = BuildPortfolioComponents(existing, normalizedCurrency, start.Value, today);
            components[today] = (
                currentAssets.GetValueOrDefault(normalizedCurrency),
                currentLiabilities.GetValueOrDefault(normalizedCurrency));

            var runningAccounts = currentAccounts;
            for (var day = today; day >= start.Value; day = day.AddDays(-1))
            {
                var key = (day, normalizedCurrency);
                if (!existingByKey.TryGetValue(key, out var snapshot))
                {
                    snapshot = new NetWorthSnapshot
                    {
                        FullWorthSpaceId = fullWorthSpaceId,
                        UserId = userId,
                        Date = day,
                        Currency = normalizedCurrency,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.NetWorthSnapshots.Add(snapshot);
                    existingByKey[key] = snapshot;
                }

                var component = components.GetValueOrDefault(day);
                snapshot.Accounts = runningAccounts;
                snapshot.Assets = component.Assets;
                snapshot.Liabilities = component.Liabilities;
                snapshot.NetWorth = snapshot.Accounts + snapshot.Assets - snapshot.Liabilities;
                result.Add(snapshot);

                if (dailyDelta.TryGetValue(day, out var delta)) runningAccounts -= delta;
                if (day == start.Value) break;
            }
        }

        await db.SaveChangesAsync(ct);
        if (wealth is not null)
            await wealth.PersistTodaySnapshotComponentsAsync(userId, fullWorthSpaceId, ct);
        return result.OrderBy(snapshot => snapshot.Date).ThenBy(snapshot => snapshot.Currency).ToList();
    }

    private static Dictionary<DateOnly, (decimal Assets, decimal Liabilities)> BuildPortfolioComponents(
        IReadOnlyCollection<NetWorthSnapshot> existing,
        string currency,
        DateOnly start,
        DateOnly today)
    {
        var points = existing
            .Where(snapshot => string.Equals(snapshot.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(snapshot => snapshot.Date)
            .ToDictionary(snapshot => snapshot.Date, snapshot => (snapshot.Assets, snapshot.Liabilities));
        var result = new Dictionary<DateOnly, (decimal Assets, decimal Liabilities)>();
        var carried = (Assets: 0m, Liabilities: 0m);
        for (var day = start; day <= today; day = day.AddDays(1))
        {
            if (points.TryGetValue(day, out var point)) carried = point;
            result[day] = carried;
            if (day == today) break;
        }
        return result;
    }

    private static int BalanceRank(string? type) => type switch
    {
        "interimAvailable" => 0,
        "closingAvailable" => 1,
        "closingBooked" => 2,
        "interimBooked" => 3,
        "expected" => 4,
        _ => 5
    };

    private static DateOnly? Min(DateOnly? left, DateOnly? right)
    {
        if (!left.HasValue) return right;
        if (!right.HasValue) return left;
        return left.Value <= right.Value ? left : right;
    }

    private sealed record HistoricalAccount(Guid Id, string Currency);
    private sealed record HistoricalTransaction(Guid AccountId, DateOnly Date, decimal Amount, string Currency);
}

public sealed class NetWorthSnapshotWorker(IServiceScopeFactory scopes, ILogger<NetWorthSnapshotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var repairHistory = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<NetWorthSnapshotService>();
                if (repairHistory)
                {
                    await service.RebuildAllHistoryAsync(stoppingToken);
                    repairHistory = false;
                }
                else
                {
                    await service.CaptureTodayAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Net worth snapshot refresh failed."); }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
