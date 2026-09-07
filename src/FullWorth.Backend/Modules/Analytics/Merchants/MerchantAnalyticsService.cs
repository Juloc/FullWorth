using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics.Merchants;

public sealed record MerchantCategorySlice(Guid? CategoryId, string Name, decimal Amount);

public sealed record MerchantAnalyticsItem(
    Guid? MerchantId,
    string Merchant,
    decimal CurrentSpend,
    int CurrentCount,
    decimal CurrentAverage,
    decimal PreviousSpend,
    decimal TrendAbsolute,
    decimal TrendPercent,
    List<MerchantCategorySlice> Categories);

public sealed record MerchantAnalyticsResult(int Year, int Month, string Currency, int Top, List<MerchantAnalyticsItem> Merchants, bool Incomplete, DateOnly? From = null, DateOnly? To = null, string Granularity = "month");

/// <summary>
/// Per-merchant spend with canonical registry identity where available. Registry names and aliases
/// resolve to one MerchantId so analytics aggregates and transaction drill-down use the same identity.
/// </summary>
public sealed class MerchantAnalyticsService(FullWorthDbContext db, FullWorth.Backend.Modules.Fx.CurrencyConverter fx)
{
    public async Task<MerchantAnalyticsResult?> MerchantSpendForUserAsync(
        Guid userId, Guid fullWorthSpaceId, int year, int month, string currency, int top, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var currentStart = new DateOnly(year, month, 1);
        var currentEnd = currentStart.AddMonths(1).AddDays(-1);
        return await BuildForWindowsAsync(
            userId, fullWorthSpaceId, currentStart, currentEnd,
            currentStart.AddMonths(-1), currentStart.AddDays(-1),
            "month", NormalizeCurrency(currency), Math.Clamp(top, 1, 100), null, null, ct);
    }

    public async Task<MerchantAnalyticsResult?> MerchantSpendForRangeForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly from,
        DateOnly to,
        string granularity,
        string currency,
        int top,
        Guid? accountId,
        Guid? accountGroupId,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        if (to < from) (from, to) = (to, from);
        var days = Math.Max(1, to.DayNumber - from.DayNumber + 1);
        return await BuildForWindowsAsync(
            userId, fullWorthSpaceId, from, to, from.AddDays(-days), from.AddDays(-1),
            NormalizeGranularity(granularity), NormalizeCurrency(currency), Math.Clamp(top, 1, 100),
            accountId, accountGroupId, ct);
    }

    private async Task<MerchantAnalyticsResult> BuildForWindowsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly currentStart,
        DateOnly currentEnd,
        DateOnly previousStart,
        DateOnly previousEnd,
        string granularity,
        string currency,
        int top,
        Guid? accountId,
        Guid? accountGroupId,
        CancellationToken ct)
    {
        var rows = await ExpensesAsync(userId, fullWorthSpaceId, previousStart, currentEnd, accountId, accountGroupId, ct);
        var current = rows.Where(row => row.Date >= currentStart && row.Date <= currentEnd).ToList();
        var previous = rows.Where(row => row.Date >= previousStart && row.Date <= previousEnd).ToList();

        var acc = new FullWorth.Backend.Modules.Fx.FxAccumulator(await fx.PrepareAsync(currency, previousStart, currentEnd, ct));
        decimal? BaseSpend(ExpenseRow row) => acc.Convert(Math.Abs(row.Amount), row.Currency, row.Date);

        var resolver = await LoadMerchantResolverAsync(fullWorthSpaceId, ct);
        var identityByTx = rows.ToDictionary(row => row.Id, row => resolver.Resolve(row.Normalized, row.Counterparty));

        var previousByMerchant = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in previous)
        {
            var converted = BaseSpend(row);
            if (!converted.HasValue) continue;
            var key = identityByTx[row.Id].GroupKey;
            previousByMerchant[key] = previousByMerchant.GetValueOrDefault(key) + converted.Value;
        }

        var currentBaseById = new Dictionary<Guid, decimal>();
        foreach (var row in current)
        {
            var converted = BaseSpend(row);
            if (converted.HasValue) currentBaseById[row.Id] = converted.Value;
        }

        var currentByMerchant = current
            .Where(row => currentBaseById.ContainsKey(row.Id))
            .Select(row => new { Row = row, Identity = identityByTx[row.Id] })
            .GroupBy(item => item.Identity.GroupKey, StringComparer.Ordinal)
            .Select(group => new
            {
                Identity = group.First().Identity,
                Spend = group.Sum(item => currentBaseById[item.Row.Id]),
                Count = group.Count(),
                TxIds = group.Select(item => item.Row.Id).ToList()
            })
            .OrderByDescending(item => item.Spend)
            .ThenBy(item => item.Identity.DisplayName)
            .Take(top)
            .ToList();

        var topTxIds = currentByMerchant.SelectMany(item => item.TxIds).ToHashSet();
        var merchantByTx = current.Where(row => topTxIds.Contains(row.Id))
            .ToDictionary(row => row.Id, row => identityByTx[row.Id].GroupKey);

        var (allocations, distributionIncomplete) = await new ExpenseAllocationBuilder(db).BuildAsync(
            fullWorthSpaceId,
            current.Where(row => topTxIds.Contains(row.Id))
                .Select(row => new ExpenseTx(row.Id, row.Amount, row.CategoryId, row.Currency, row.Date)).ToList(),
            fx, currency, ct);
        var categoryNames = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(category => category.Id, category => category.Name, ct);

        var distribution = new Dictionary<string, Dictionary<Guid, decimal>>(StringComparer.Ordinal);
        foreach (var allocation in allocations)
        {
            if (!merchantByTx.TryGetValue(allocation.TransactionId, out var merchantKey)) continue;
            var categoryKey = allocation.CategoryId ?? Guid.Empty;
            if (!distribution.TryGetValue(merchantKey, out var slices))
                distribution[merchantKey] = slices = new Dictionary<Guid, decimal>();
            slices[categoryKey] = slices.GetValueOrDefault(categoryKey) + allocation.Amount;
        }

        var items = currentByMerchant.Select(item =>
        {
            var previousSpend = previousByMerchant.GetValueOrDefault(item.Identity.GroupKey);
            var trendAbsolute = item.Spend - previousSpend;
            var trendPercent = previousSpend == 0m
                ? (item.Spend == 0m ? 0m : 100m)
                : trendAbsolute / previousSpend * 100m;
            var categories = distribution.TryGetValue(item.Identity.GroupKey, out var slices)
                ? slices.Select(slice => new MerchantCategorySlice(
                        slice.Key == Guid.Empty ? null : slice.Key,
                        slice.Key != Guid.Empty && categoryNames.TryGetValue(slice.Key, out var name) ? name : "Uncategorized",
                        Round(slice.Value)))
                    .OrderByDescending(slice => slice.Amount).ToList()
                : [];

            return new MerchantAnalyticsItem(
                item.Identity.MerchantId,
                item.Identity.DisplayName,
                Round(item.Spend),
                item.Count,
                Round(item.Count == 0 ? 0m : item.Spend / item.Count),
                Round(previousSpend),
                Round(trendAbsolute),
                Round(trendPercent),
                categories);
        }).ToList();

        return new MerchantAnalyticsResult(
            currentStart.Year, currentStart.Month, currency, top, items,
            acc.Incomplete || distributionIncomplete, currentStart, currentEnd, granularity);
    }

    private async Task<MerchantResolver> LoadMerchantResolverAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var merchantRows = await db.Merchants.AsNoTracking()
            .Where(merchant => merchant.FullWorthSpaceId == fullWorthSpaceId)
            .Select(merchant => new { merchant.Id, merchant.Name, merchant.NormalizedName })
            .ToListAsync(ct);
        var aliases = await db.MerchantAliases.AsNoTracking()
            .Where(alias => alias.FullWorthSpaceId == fullWorthSpaceId)
            .Select(alias => new { alias.MerchantId, alias.NormalizedAlias })
            .ToListAsync(ct);

        var names = merchantRows.ToDictionary(merchant => merchant.Id, merchant => merchant.Name);
        var matchers = new List<MerchantMatcher>();
        matchers.AddRange(merchantRows
            .Where(merchant => !string.IsNullOrWhiteSpace(merchant.NormalizedName))
            .Select(merchant => new MerchantMatcher(merchant.NormalizedName, merchant.Id, merchant.Name)));
        matchers.AddRange(aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias.NormalizedAlias) && names.ContainsKey(alias.MerchantId))
            .Select(alias => new MerchantMatcher(alias.NormalizedAlias, alias.MerchantId, names[alias.MerchantId])));
        return new MerchantResolver(matchers.OrderByDescending(matcher => matcher.Key.Length).ToList());
    }

    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);

    private async Task<List<ExpenseRow>> ExpensesAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly from,
        DateOnly to,
        Guid? accountId,
        Guid? accountGroupId,
        CancellationToken ct) =>
        await db.Transactions.AsNoTracking()
            .Where(transaction =>
                transaction.Amount < 0 &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                transaction.Status != "PDNG" &&
                transaction.BookingDate != null &&
                transaction.BookingDate >= from &&
                transaction.BookingDate <= to &&
                db.Accounts.Any(account =>
                    account.Id == transaction.AccountId &&
                    account.FullWorthSpaceId == fullWorthSpaceId &&
                    (!accountId.HasValue || account.Id == accountId.Value) &&
                    (!accountGroupId.HasValue || account.GroupId == accountGroupId.Value) &&
                    account.Owners.Any(owner => owner.UserId == userId)))
            .Select(transaction => new ExpenseRow(
                transaction.Id, transaction.Amount, transaction.CategoryId,
                transaction.NormalizedCounterparty, transaction.Counterparty,
                transaction.Currency, transaction.BookingDate!.Value))
            .ToListAsync(ct);

    private static string NormalizeGranularity(string? granularity)
    {
        var normalized = (granularity ?? "month").Trim().ToLowerInvariant();
        return normalized is "week" or "month" or "quarter" or "year" ? normalized : "month";
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string NormalizeCurrency(string currency)
    {
        var normalized = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(character => character is >= 'A' and <= 'Z') ? normalized : "EUR";
    }

    private sealed record ExpenseRow(Guid Id, decimal Amount, Guid? CategoryId, string? Normalized, string? Counterparty, string Currency, DateOnly Date);
    private sealed record MerchantMatcher(string Key, Guid MerchantId, string DisplayName);
    private sealed record MerchantIdentity(Guid? MerchantId, string DisplayName, string GroupKey);

    private sealed class MerchantResolver(IReadOnlyList<MerchantMatcher> matchers)
    {
        public MerchantIdentity Resolve(string? normalized, string? counterparty)
        {
            var candidate = MerchantNormalization.Normalize(normalized ?? counterparty);
            if (candidate is not null)
            {
                var match = matchers.FirstOrDefault(item => candidate.Contains(item.Key, StringComparison.Ordinal));
                if (match is not null)
                    return new MerchantIdentity(match.MerchantId, match.DisplayName, "merchant:" + match.MerchantId.ToString("N"));
            }

            var display = !string.IsNullOrWhiteSpace(counterparty)
                ? counterparty.Trim()
                : !string.IsNullOrWhiteSpace(normalized) ? normalized.Trim() : "Unknown";
            var fallback = MerchantNormalization.Normalize(display) ?? display.ToUpperInvariant();
            return new MerchantIdentity(null, display, "text:" + fallback);
        }
    }
}

public static class MerchantAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapMerchantAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/merchants", async (
            Guid fullWorthSpaceId,
            int? year,
            int? month,
            DateOnly? from,
            DateOnly? to,
            string? granularity,
            string? currency,
            int? top,
            Guid? accountId,
            Guid? accountGroupId,
            CurrentUserContext currentUser,
            MerchantAnalyticsService service,
            CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            MerchantAnalyticsResult? result;
            if (from.HasValue || to.HasValue)
            {
                var resolvedTo = to ?? today;
                var resolvedFrom = from ?? resolvedTo.AddMonths(-1).AddDays(1);
                result = await service.MerchantSpendForRangeForUserAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, resolvedFrom, resolvedTo,
                    granularity ?? "month", currency ?? "EUR", top ?? 10, accountId, accountGroupId, ct);
            }
            else
            {
                result = await service.MerchantSpendForUserAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, year ?? today.Year, month ?? today.Month,
                    currency ?? "EUR", top ?? 10, ct);
            }
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Analytics");

        return app;
    }
}