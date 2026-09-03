using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics;

public sealed record UpcomingContractItem(Guid Id, string Name, decimal Amount, string Currency, DateOnly? NextDueDate, Guid? AccountId);
public sealed record DashboardResult(string Currency, decimal Accounts, decimal Assets, decimal Liabilities, decimal NetWorth, decimal Income, decimal Expenses, bool Incomplete, List<UpcomingContractItem> Upcoming);
public sealed record BudgetStatusItem(Guid Id, string Name, Guid? CategoryId, string Period, DateOnly PeriodStart, DateOnly PeriodEnd, decimal Amount, decimal Spent, decimal Remaining, decimal Percent);
public sealed record ForecastPoint(DateOnly Date, decimal EstimatedNetWorth, decimal AverageHistoricalNet, decimal RecurringMonthly);
public sealed record ExpenseAllocation(Guid TransactionId, Guid? CategoryId, decimal Amount, bool FromPurchaseItem);
// Guided chart builder (§15.2). A bounded measure×dimension query over the same FX-aware aggregation.
public sealed record ChartPoint(string? Key, string Label, decimal Value);
public sealed record ChartResult(string Currency, string Measure, string Dimension, bool Incomplete, IReadOnlyList<ChartPoint> Series);

public sealed class AnalyticsService(FullWorthDbContext db, FullWorth.Backend.Modules.Fx.CurrencyConverter fx)
{
    public async Task<object?> OverviewForUserAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, string currency, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        currency = NormalizeCurrency(currency);
        var today = DateOnly.FromDateTime(DateTime.Today);

        // §18: include foreign-currency transactions and convert each to the base currency at the rate
        // effective on its value date (historical, not today's), instead of the old `Currency == base`
        // filter that silently dropped them. A missing rate marks the result incomplete and drops that row.
        var query = AccessibleTransactions(userId, fullWorthSpaceId)
            .Where(transaction => !transaction.IsIgnored && !transaction.IsTransfer && transaction.Status != "PDNG");
        if (from.HasValue) query = query.Where(transaction => transaction.BookingDate >= from.Value);
        if (to.HasValue) query = query.Where(transaction => transaction.BookingDate <= to.Value);

        var transactions = await query.Select(transaction => new { transaction.Id, transaction.BookingDate, transaction.ValueDate, transaction.Amount, transaction.Currency, transaction.CategoryId, transaction.RefundOfTransactionId }).ToListAsync(ct);
        var txMeta = transactions.ToDictionary(t => t.Id, t => (t.Currency, Date: t.BookingDate ?? t.ValueDate ?? today));
        var anchorDates = txMeta.Values.Select(m => m.Date).ToList();
        var acc = new Fx.FxAccumulator(await fx.PrepareAsync(currency, MinDate(anchorDates, today), MaxDate(anchorDates, today), ct));
        decimal? Convert(Guid txId, decimal amount) => txMeta.TryGetValue(txId, out var m) ? acc.Convert(amount, m.Currency, m.Date) : null;

        // A linked refund is not ordinary income (§9.6) — it reduces the original expense's category
        // via the allocation builder, so exclude it from the income total here. Convert each per value date.
        var income = 0m;
        foreach (var transaction in transactions.Where(t => t.Amount > 0 && t.RefundOfTransactionId == null))
        {
            var converted = Convert(transaction.Id, transaction.Amount);
            if (converted.HasValue) income += converted.Value;
        }
        // The builder converts each expense allocation AND each linked refund to base at their OWN value
        // dates (§18), so a refund across an FX move nets correctly. It returns base-currency allocations.
        var (allocations, allocationsIncomplete) = await BuildExpenseAllocationsAsync(
            fullWorthSpaceId,
            transactions.Where(transaction => transaction.Amount < 0)
                .Select(transaction => new ExpenseTx(transaction.Id, transaction.Amount, transaction.CategoryId, transaction.Currency, transaction.BookingDate ?? transaction.ValueDate ?? today)).ToList(),
            currency, ct);
        // Expenses total is derived from the (refund-netted) allocations so it stays consistent with the
        // per-category breakdown below.
        var expenses = allocations.Sum(allocation => allocation.Amount);
        var categoryNames = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(category => category.Id, category => category.Name, ct);
        var byCategory = allocations.GroupBy(allocation => allocation.CategoryId).Select(group => new
        {
            categoryId = group.Key,
            category = group.Key.HasValue && categoryNames.TryGetValue(group.Key.Value, out var name) ? name : "Uncategorized",
            amount = group.Sum(allocation => allocation.Amount),
            count = group.Select(allocation => allocation.TransactionId).Distinct().Count(),
            itemBreakdown = group.Any(allocation => allocation.FromPurchaseItem)
        }).OrderByDescending(item => item.amount).ToList();

        // byMonth reconciles with the headline: monthly expenses come from the same refund-netted
        // allocations (attributed to each original expense's month), and monthly income excludes linked
        // refunds exactly like the headline income. So sum(byMonth.expenses)==expenses and
        // sum(byMonth.net)==net.
        var monthOfTx = transactions.Where(t => t.BookingDate.HasValue)
            .ToDictionary(t => t.Id, t => (t.BookingDate!.Value.Year, t.BookingDate!.Value.Month));
        var monthlyExpense = new Dictionary<(int Year, int Month), decimal>();
        foreach (var allocation in allocations)
        {
            if (!monthOfTx.TryGetValue(allocation.TransactionId, out var ym)) continue;
            monthlyExpense[ym] = monthlyExpense.GetValueOrDefault(ym) + allocation.Amount;
        }
        var monthlyIncome = new Dictionary<(int Year, int Month), decimal>();
        foreach (var t in transactions.Where(t => t.BookingDate.HasValue && t.Amount > 0 && t.RefundOfTransactionId == null))
        {
            var converted = Convert(t.Id, t.Amount);
            if (!converted.HasValue) continue;
            var ym = (t.BookingDate!.Value.Year, t.BookingDate!.Value.Month);
            monthlyIncome[ym] = monthlyIncome.GetValueOrDefault(ym) + converted.Value;
        }
        var byMonth = monthlyExpense.Keys.Union(monthlyIncome.Keys)
            .Select(ym => new
            {
                year = ym.Year,
                month = ym.Month,
                income = monthlyIncome.GetValueOrDefault(ym),
                expenses = monthlyExpense.GetValueOrDefault(ym),
                net = monthlyIncome.GetValueOrDefault(ym) - monthlyExpense.GetValueOrDefault(ym)
            })
            .OrderBy(item => item.year).ThenBy(item => item.month).ToList();

        return new { currency, income, expenses, net = income - expenses, byCategory, byMonth, incomplete = acc.Incomplete || allocationsIncomplete };
    }

    public async Task<DashboardResult?> DashboardForUserAsync(Guid userId, Guid fullWorthSpaceId, string currency, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        currency = NormalizeCurrency(currency);

        var today = DateOnly.FromDateTime(DateTime.Today);

        // Net worth is a cross-currency total (§18): convert every balance/value into the base currency
        // at the latest available rate instead of the old `Currency == base` filter that silently
        // dropped foreign holdings. A missing rate marks the total incomplete — never assumed 1:1.
        var incomplete = false;
        var rates = await fx.PrepareLatestAsync(currency, today, ct);
        decimal SumInBase(IEnumerable<(decimal Amount, string Currency)> items)
        {
            var total = 0m;
            foreach (var (amount, itemCurrency) in items)
            {
                var converted = rates.ToBaseOn(amount, itemCurrency, today);
                if (converted is null) incomplete = true; else total += converted.Value;
            }
            return total;
        }

        var accountBalances = await AccessibleAccounts(userId, fullWorthSpaceId)
            .Where(account => account.IsActive && account.IncludeInNetWorth)
            .Select(account => new
            {
                account.Currency,
                // Inlined CurrentFirst ordering — correlated subquery, where EF cannot expand the
                // extension (see BalanceSnapshotQueries.CurrentFirst). Newest capture, then rank-prefix + type.
                Amount = db.BalanceSnapshots
                    .Where(balance => balance.AccountId == account.Id)
                    .OrderByDescending(balance => balance.CapturedAt)
                    .ThenBy(balance => (balance.BalanceType == "interimAvailable" ? "0"
                                      : balance.BalanceType == "closingAvailable" ? "1"
                                      : balance.BalanceType == "closingBooked" ? "2"
                                      : balance.BalanceType == "interimBooked" ? "3"
                                      : balance.BalanceType == "expected" ? "4" : "5") + balance.BalanceType)
                    .Select(balance => (decimal?)balance.Amount)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);
        var accounts = SumInBase(accountBalances.Where(x => x.Amount.HasValue).Select(x => (x.Amount!.Value, x.Currency)));

        var assetRows = await db.Assets.AsNoTracking()
            .Where(asset => asset.FullWorthSpaceId == fullWorthSpaceId && asset.IncludeInNetWorth)
            .Select(asset => new { asset.CurrentValue, asset.Currency })
            .ToListAsync(ct);
        var assets = SumInBase(assetRows.Select(a => (a.CurrentValue, a.Currency)));

        var liabilityRows = await db.Liabilities.AsNoTracking()
            .Where(liability => liability.FullWorthSpaceId == fullWorthSpaceId && liability.IncludeInNetWorth)
            .Select(liability => new { liability.CurrentBalance, liability.Currency })
            .ToListAsync(ct);
        var liabilities = SumInBase(liabilityRows.Select(l => (l.CurrentBalance, l.Currency)));

        // Current-month income vs expenses for the §8.4 widget, using the same rules as the analytics
        // overview: transfers and 'exclude from statistics' are dropped, a linked refund is not income
        // (it nets the original expense via the allocation builder), and expenses come from the
        // refund-netted per-category allocations (positive magnitudes).
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthTx = await AccessibleTransactions(userId, fullWorthSpaceId)
            .Where(transaction => !transaction.IsIgnored && !transaction.IsTransfer
                && transaction.Status != "PDNG" && transaction.BookingDate >= monthStart && transaction.BookingDate <= today)
            .Select(transaction => new { transaction.Id, transaction.BookingDate, transaction.ValueDate, transaction.Amount, transaction.Currency, transaction.CategoryId, transaction.RefundOfTransactionId })
            .ToListAsync(ct);
        // Convert the month's foreign transactions at their value-date rate; a separate snapshot spans the
        // whole month (the net-worth `rates` snapshot only covers the last 14 days). Missing rates fold
        // into the same dashboard `incomplete` flag as the net-worth total.
        var monthMeta = monthTx.ToDictionary(t => t.Id, t => (t.Currency, Date: t.BookingDate ?? t.ValueDate ?? today));
        var monthAcc = new Fx.FxAccumulator(await fx.PrepareAsync(currency, monthStart, today, ct));
        decimal? ConvertMonth(Guid txId, decimal amount) => monthMeta.TryGetValue(txId, out var m) ? monthAcc.Convert(amount, m.Currency, m.Date) : null;
        var income = 0m;
        foreach (var transaction in monthTx.Where(t => t.Amount > 0 && t.RefundOfTransactionId == null))
        {
            var converted = ConvertMonth(transaction.Id, transaction.Amount);
            if (converted.HasValue) income += converted.Value;
        }
        var (monthAllocations, monthAllocationsIncomplete) = await BuildExpenseAllocationsAsync(
            fullWorthSpaceId,
            monthTx.Where(transaction => transaction.Amount < 0).Select(transaction => new ExpenseTx(transaction.Id, transaction.Amount, transaction.CategoryId, transaction.Currency, transaction.BookingDate ?? transaction.ValueDate ?? today)).ToList(),
            currency, ct);
        var expenses = monthAllocations.Sum(allocation => allocation.Amount);
        if (monthAcc.Incomplete || monthAllocationsIncomplete) incomplete = true;

        var upcoming = await VisibleContracts(userId, fullWorthSpaceId)
            .Where(contract => contract.IsActive && contract.Currency == currency && contract.NextDueDate >= today && contract.NextDueDate <= today.AddDays(30))
            .OrderBy(contract => contract.NextDueDate)
            .Take(20)
            .Select(contract => new UpcomingContractItem(contract.Id, contract.Name, contract.Amount, contract.Currency, contract.NextDueDate, contract.AccountId))
            .ToListAsync(ct);
        return new(currency, accounts, assets, liabilities, accounts + assets - liabilities, income, expenses, incomplete, upcoming);
    }

    /// <summary>
    /// Space-wide budget status for every ACTIVE budget regardless of cycle type (§12.2 — calendar
    /// month, pay-cycle, weekly/bi-weekly and custom budgets must all be visible, not just monthly
    /// ones). Each budget resolves its OWN current window via <see cref="BudgetCycleResolver"/>
    /// instead of being forced into the caller's calendar-month range. <paramref name="year"/>/
    /// <paramref name="month"/> pick the reference date: the actual current day when they name the
    /// current calendar month (so a mid-cycle pay-cycle/weekly budget reports its true in-progress
    /// window), otherwise the 1st of the requested month.
    /// </summary>
    public async Task<object?> BudgetStatusForUserAsync(Guid userId, Guid fullWorthSpaceId, int year, int month, string currency, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        currency = NormalizeCurrency(currency);

        var budgets = await db.Budgets.AsNoTracking()
            .Where(budget => budget.FullWorthSpaceId == fullWorthSpaceId && budget.IsActive && budget.Currency == currency)
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var reference = year == today.Year && month == today.Month ? today : new DateOnly(year, month, 1);

        // Resolve each budget's current window once, then prepare one FX snapshot spanning all of them.
        // Budgets are filtered to the base `currency`, but a budget's SPEND may include foreign
        // transactions — those are converted INTO the budget currency at each transaction's value date.
        var windowByBudget = new Dictionary<Guid, (DateOnly Start, DateOnly End)>();
        foreach (var budget in budgets)
        {
            var cycle = Budgets.Cycles.BudgetCycleResolver.Resolve(budget.Period, budget.StartDate, budget.EndDate);
            var period = Budgets.Cycles.BudgetCycleCalculator.CurrentPeriod(cycle, reference);
            windowByBudget[budget.Id] = (period.Start, period.End);
        }
        var items = new List<BudgetStatusItem>();
        var incomplete = false;
        // Budgets sharing a window (e.g. several monthly budgets) share one transaction scan. Spend in a
        // foreign currency is converted INTO the budget currency at each transaction's value-date rate.
        var allocationsByWindow = new Dictionary<(DateOnly Start, DateOnly End), List<ExpenseAllocation>>();
        foreach (var budget in budgets)
        {
            var window = windowByBudget[budget.Id];
            var key = (window.Start, window.End);
            if (!allocationsByWindow.TryGetValue(key, out var allocations))
            {
                var rows = await AccessibleTransactions(userId, fullWorthSpaceId)
                    .Where(transaction => !transaction.IsIgnored && !transaction.IsTransfer && transaction.Amount < 0 && transaction.BookingDate >= key.Start && transaction.BookingDate <= key.End)
                    .Select(transaction => new { transaction.Id, transaction.BookingDate, transaction.ValueDate, transaction.Amount, transaction.Currency, transaction.CategoryId })
                    .ToListAsync(ct);
                var (built, windowIncomplete) = await BuildExpenseAllocationsAsync(
                    fullWorthSpaceId,
                    rows.Select(t => new ExpenseTx(t.Id, t.Amount, t.CategoryId, t.Currency, t.BookingDate ?? t.ValueDate ?? reference)).ToList(),
                    currency, ct);
                if (windowIncomplete) incomplete = true;
                allocations = built;
                allocationsByWindow[key] = allocations;
            }

            var spent = allocations.Where(allocation => !budget.CategoryId.HasValue || allocation.CategoryId == budget.CategoryId).Sum(allocation => allocation.Amount);
            items.Add(new BudgetStatusItem(
                budget.Id,
                budget.Name,
                budget.CategoryId,
                budget.Period,
                window.Start,
                window.End,
                budget.Amount,
                spent,
                budget.Amount - spent,
                budget.Amount == 0 ? 0 : spent / budget.Amount * 100m));
        }
        return new { year, month, currency, items, incomplete };
    }

    public async Task<object?> ForecastForUserAsync(Guid userId, Guid fullWorthSpaceId, int months, string currency, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        months = Math.Clamp(months, 1, 60);
        currency = NormalizeCurrency(currency);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var historyFrom = today.AddMonths(-6);
        var rows = await AccessibleTransactions(userId, fullWorthSpaceId)
            .Where(transaction => !transaction.IsIgnored && !transaction.IsTransfer && transaction.BookingDate >= historyFrom && transaction.BookingDate != null)
            .Select(transaction => new { Date = transaction.BookingDate!.Value, transaction.Amount, transaction.Currency })
            .ToListAsync(ct);
        // Convert each historical month's transactions to base at their booking-date rate before averaging.
        var forecastAcc = new Fx.FxAccumulator(await fx.PrepareAsync(currency, rows.Count > 0 ? rows.Min(r => r.Date) : today, rows.Count > 0 ? rows.Max(r => r.Date) : today, ct));
        var monthlyNet = new Dictionary<(int Year, int Month), decimal>();
        foreach (var row in rows)
        {
            var converted = forecastAcc.Convert(row.Amount, row.Currency, row.Date);
            if (!converted.HasValue) continue;
            var ym = (row.Date.Year, row.Date.Month);
            monthlyNet[ym] = monthlyNet.GetValueOrDefault(ym) + converted.Value;
        }
        var averageNet = monthlyNet.Count == 0 ? 0m : monthlyNet.Values.Average();
        var dashboard = await DashboardForUserAsync(userId, fullWorthSpaceId, currency, ct);
        if (dashboard is null) return null;
        var contracts = await VisibleContracts(userId, fullWorthSpaceId)
            .Where(contract => contract.IsActive && contract.Currency == currency)
            .Select(contract => new { contract.Amount, contract.BillingCycle, contract.Interval })
            .ToListAsync(ct);
        var recurringMonthly = contracts.Sum(contract => MonthlyEquivalent(contract.Amount, contract.BillingCycle, contract.Interval));
        var points = new List<ForecastPoint>();
        var value = dashboard.NetWorth;
        for (var i = 1; i <= months; i++)
        {
            var date = today.AddMonths(i);
            value += averageNet;
            points.Add(new(date, value, averageNet, recurringMonthly));
        }
        return new
        {
            currency,
            currentNetWorth = dashboard.NetWorth,
            averageHistoricalNet = averageNet,
            knownRecurringMonthly = recurringMonthly,
            months,
            points,
            incomplete = forecastAcc.Incomplete || dashboard.Incomplete
        };
    }

    // Guided chart builder (§15.2): a bounded measure×dimension query reusing the same FX-aware
    // aggregation as Overview (materialize rows, then aggregate + convert in memory — Npgsql-safe).
    public async Task<ChartResult?> ChartForUserAsync(Guid userId, Guid fullWorthSpaceId, string measure, string dimension, DateOnly from, DateOnly to, string currency, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        currency = NormalizeCurrency(currency);
        measure = OneOf(measure, "spend", "income", "net", "count");
        dimension = OneOf(dimension, "month", "category", "merchant", "none");

        var rows = await AccessibleTransactions(userId, fullWorthSpaceId)
            .Where(t => !t.IsIgnored && !t.IsTransfer && t.Status != "PDNG" && t.BookingDate != null && t.BookingDate >= from && t.BookingDate <= to)
            .Select(t => new { t.Id, Date = t.BookingDate!.Value, t.Amount, t.Currency, t.CategoryId, t.NormalizedCounterparty, t.Counterparty, t.RefundOfTransactionId })
            .ToListAsync(ct);

        var categoryNames = await db.Categories.AsNoTracking()
            .Where(c => c.FullWorthSpaceId == fullWorthSpaceId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        string CatLabel(Guid? id) => id.HasValue && categoryNames.TryGetValue(id.Value, out var name) ? name : "Uncategorized";
        var txMeta = rows.ToDictionary(r => r.Id, r => (Merchant: MerchantKey(r.NormalizedCounterparty, r.Counterparty), Month: $"{r.Date.Year:D4}-{r.Date.Month:D2}"));

        // Dimension key+label for a transaction (income/count) or an allocation (spend — uses its split category).
        (string Key, string Label) Dim(Guid? categoryId, Guid txId) => dimension switch
        {
            "category" => (categoryId?.ToString() ?? "", CatLabel(categoryId)),
            "merchant" => (txMeta[txId].Merchant, txMeta[txId].Merchant),
            "month" => (txMeta[txId].Month, txMeta[txId].Month),
            _ => ("total", "Total")
        };

        var buckets = new Dictionary<string, (string Label, decimal Value)>();
        void Add(string key, string label, decimal value)
        {
            var current = buckets.GetValueOrDefault(key);
            buckets[key] = (label, current.Value + value);
        }

        var incomplete = false;
        if (measure is "spend" or "net")
        {
            var (allocations, allocationsIncomplete) = await BuildExpenseAllocationsAsync(
                fullWorthSpaceId,
                rows.Where(r => r.Amount < 0).Select(r => new ExpenseTx(r.Id, r.Amount, r.CategoryId, r.Currency, r.Date)).ToList(),
                currency, ct);
            incomplete |= allocationsIncomplete;
            var sign = measure == "net" ? -1m : 1m; // spend reduces net
            foreach (var allocation in allocations)
            {
                var (key, label) = Dim(allocation.CategoryId, allocation.TransactionId);
                Add(key, label, sign * allocation.Amount);
            }
        }
        if (measure is "income" or "net")
        {
            var dates = rows.Select(r => r.Date).ToList();
            var acc = new Fx.FxAccumulator(await fx.PrepareAsync(currency, dates.Count > 0 ? dates.Min() : from, dates.Count > 0 ? dates.Max() : to, ct));
            foreach (var r in rows.Where(r => r.Amount > 0 && r.RefundOfTransactionId == null))
            {
                var converted = acc.Convert(r.Amount, r.Currency, r.Date);
                if (!converted.HasValue) continue;
                var (key, label) = Dim(r.CategoryId, r.Id);
                Add(key, label, converted.Value);
            }
            incomplete |= acc.Incomplete;
        }
        if (measure == "count")
            foreach (var r in rows)
            {
                var (key, label) = Dim(r.CategoryId, r.Id);
                Add(key, label, 1m);
            }

        // Chronological for month; otherwise largest by MAGNITUDE first (so a "net by category" chart
        // surfaces the biggest spend categories, which are negative, not just the biggest income), capped
        // so donut/hbar stay readable. For all-positive measures magnitude == value, so this is unchanged.
        var ordered = dimension == "month" ? buckets.OrderBy(b => b.Key).AsEnumerable() : buckets.OrderByDescending(b => Math.Abs(b.Value.Value)).Take(12);
        var series = ordered.Select(b => new ChartPoint(string.IsNullOrEmpty(b.Key) ? null : b.Key, b.Value.Label, Math.Round(b.Value.Value, 2, MidpointRounding.AwayFromZero))).ToList();
        return new ChartResult(currency, measure, dimension, incomplete, series);
    }

    private static string OneOf(string value, string fallback, params string[] allowed)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return v == fallback || allowed.Contains(v) ? v : fallback;
    }

    private static string MerchantKey(string? normalized, string? counterparty) =>
        !string.IsNullOrWhiteSpace(normalized) ? normalized.Trim()
        : !string.IsNullOrWhiteSpace(counterparty) ? counterparty.Trim()
        : "Unknown";

    private IQueryable<FullWorth.Backend.Modules.Accounts.FinanceAccount> AccessibleAccounts(Guid userId, Guid fullWorthSpaceId) =>
        db.Accounts.AsNoTracking().Where(account =>
            account.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId));

    private IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction> AccessibleTransactions(Guid userId, Guid fullWorthSpaceId) =>
        db.Transactions.AsNoTracking().Where(transaction => db.Accounts.Any(account =>
            account.Id == transaction.AccountId &&
            account.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId)));

    private IQueryable<FullWorth.Backend.Modules.Contracts.RecurringContract> VisibleContracts(Guid userId, Guid fullWorthSpaceId) =>
        db.Contracts.AsNoTracking().Where(contract =>
            contract.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            (contract.AccountId == null || db.AccountOwners.Any(owner => owner.AccountId == contract.AccountId.Value && owner.UserId == userId)));

    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);

    private Task<(List<ExpenseAllocation> Allocations, bool Incomplete)> BuildExpenseAllocationsAsync(
        Guid fullWorthSpaceId, IReadOnlyList<ExpenseTx> transactions, string baseCurrency, CancellationToken ct) =>
        new ExpenseAllocationBuilder(db).BuildAsync(fullWorthSpaceId, transactions, fx, baseCurrency, ct);

    private static string NormalizeCurrency(string currency)
    {
        var normalized = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(character => character is >= 'A' and <= 'Z') ? normalized : "EUR";
    }

    // Bounds for the FX snapshot window over a set of transaction dates (fallback when there are none).
    private static DateOnly MinDate(IReadOnlyCollection<DateOnly> dates, DateOnly fallback) => dates.Count == 0 ? fallback : dates.Min();
    private static DateOnly MaxDate(IReadOnlyCollection<DateOnly> dates, DateOnly fallback) => dates.Count == 0 ? fallback : dates.Max();

    private static decimal MonthlyEquivalent(decimal amount, string cycle, int interval) => cycle switch
    {
        "weekly" => amount * 52m / 12m / Math.Max(1, interval),
        "quarterly" => amount / (3m * Math.Max(1, interval)),
        "yearly" or "annual" => amount / (12m * Math.Max(1, interval)),
        _ => amount / Math.Max(1, interval)
    };
}

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analytics").WithTags("Analytics");

        group.MapGet("/overview", async (Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
            ToResult(await service.OverviewForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, from, to, currency ?? "EUR", ct)));

        group.MapGet("/dashboard", async (Guid fullWorthSpaceId, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
        {
            var result = await service.DashboardForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, currency ?? "EUR", ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapGet("/budget-status", async (Guid fullWorthSpaceId, int? year, int? month, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
        {
            var now = DateTime.Today;
            return ToResult(await service.BudgetStatusForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, year ?? now.Year, month ?? now.Month, currency ?? "EUR", ct));
        });

        group.MapGet("/forecast", async (Guid fullWorthSpaceId, int? months, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
            ToResult(await service.ForecastForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, months ?? 12, currency ?? "EUR", ct)));

        group.MapGet("/chart", async (Guid fullWorthSpaceId, string? measure, string? dimension, DateOnly? from, DateOnly? to, string? currency, CurrentUserContext currentUser, AnalyticsService service, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var f = from ?? today.AddMonths(-12);
            var t = to ?? today;
            if (t < f) (f, t) = (t, f);
            if (t.DayNumber - f.DayNumber > 1830) f = t.AddDays(-1830); // clamp span to ~5 years
            return ToResult(await service.ChartForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, measure ?? "spend", dimension ?? "month", f, t, currency ?? "EUR", ct));
        });
        return app;
    }

    private static IResult ToResult(object? result) => result is null ? Results.NotFound() : Results.Ok(result);
}
