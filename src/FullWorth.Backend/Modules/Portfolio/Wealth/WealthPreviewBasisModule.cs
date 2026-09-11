using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Parity;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>
/// One line of the forward preview's basis: a figure that recurs every month, named, so the screen can
/// show what the preview is built from and let the owner give each line its own expected increase.
/// </summary>
/// <param name="Kind">See <see cref="WealthPreviewLineKinds"/>.</param>
/// <param name="Id">The contract or income schedule this line is; null for the variable aggregate.</param>
/// <param name="MonthlyAmount">
/// Always positive and always in the space's base currency: the sign is carried by
/// <paramref name="Kind"/>, so a caller cannot accidentally add an expense to income.
/// </param>
/// <param name="IsEstimate">
/// True when the figure is observed rather than configured — the variable spend is an average of what
/// actually happened, not a number anybody entered, and the screen has to be able to say so.
/// </param>
public sealed record WealthPreviewLine(
    string Kind,
    Guid? Id,
    string Name,
    decimal MonthlyAmount,
    bool IsEstimate);

public static class WealthPreviewLineKinds
{
    public const string Income = "income";
    public const string FixedCost = "fixed";
    public const string VariableSpend = "variable";
}

/// <summary>
/// What the forward preview on the wealth page is built from, per month and in the base currency.
///
/// The preview used to take its savings rate from the measured net-worth curve — last value minus first
/// value, divided by the months. That is backwards in two senses: it extrapolates from the past, and it
/// bundles market movement in with actual saving, so a good year on the markets read as a high savings
/// rate and then compounded on top of itself.
///
/// This composes the figure forward instead, from things that exist: configured income, the contracts
/// the owner marked as fixed costs, and what is actually spent besides those.
///
/// <b>The double-count this had to avoid.</b> A contract is a fixed cost <i>and</i> its payment shows up
/// as a booked expense. The cashflow endpoint gets away with adding both because it compares future dues
/// against past spending — different periods. A monthly steady-state figure cannot: the same rent would
/// be subtracted twice. So the variable average excludes every transaction linked to a contract.
///
/// <b>Budgets are reported, not used.</b> <see cref="MonthlyBudgetLimit"/> is the sum of the limits and
/// exists only so the screen can put it next to the actual average. A budget is an intention; the
/// preview is about what is likely to happen, and those are different questions.
/// </summary>
public sealed record WealthPreviewBasisView(
    string Currency,
    IReadOnlyList<WealthPreviewLine> Lines,
    decimal MonthlyIncome,
    decimal MonthlyFixedCosts,
    decimal MonthlyVariableSpend,
    /// <summary>The sum of the budget limits, for comparison on screen. Never part of the arithmetic.</summary>
    decimal MonthlyBudgetLimit,
    /// <summary>Income minus fixed costs minus variable spend. What the preview grows.</summary>
    decimal MonthlySurplus,
    /// <summary>How many whole months the variable average rests on. One month is not an average.</summary>
    int ObservedMonths,
    /// <summary>False when a rate was missing: the surplus is then unknown, not merely smaller.</summary>
    bool IsComplete,
    IReadOnlyList<string> MissingCurrencies);

public static class WealthPreviewBasisModule
{
    /// <summary>Six months smooths a quarterly insurance bill without reaching back into a different life.</summary>
    private const int DefaultObservedMonths = 6;

    public static IEndpointRouteBuilder MapWealthPreviewBasisEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/wealth/preview-basis", async (
            Guid fullWorthSpaceId,
            int? months,
            CurrentUserContext currentUser,
            FullWorthDbContext db,
            CurrencyConverter converter,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();

            var basis = await BuildAsync(db, converter, userId, fullWorthSpaceId, months, ct);
            return basis is null ? Results.NotFound() : Results.Ok(basis);
        });

        return app;
    }

    internal static async Task<WealthPreviewBasisView?> BuildAsync(
        FullWorthDbContext db,
        CurrencyConverter converter,
        Guid userId,
        Guid fullWorthSpaceId,
        int? requestedMonths,
        CancellationToken ct)
    {
        var space = await db.FullWorthSpaces.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == fullWorthSpaceId, ct);
        if (space is null) return null;

        var baseCurrency = FxSnapshot.Normalize(space.BaseCurrency);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var months = Math.Clamp(requestedMonths ?? DefaultObservedMonths, 1, 36);
        // Whole months only, ending with the last day before this month: a part-month divided by a whole
        // month understates the average, and it changes every day for no reason the owner can see.
        var windowEnd = new DateOnly(today.Year, today.Month, 1);
        var windowStart = windowEnd.AddMonths(-months);

        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var fx = await converter.PrepareAsync(baseCurrency, windowStart, today.AddMonths(1), ct);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var lines = new List<WealthPreviewLine>();

        // ---- income: what is configured, not what happened to arrive ----
        var income = 0m;
        foreach (var schedule in await LoadIncomeSchedulesAsync(db, fullWorthSpaceId, visible, ct))
        {
            if (schedule.Amount is not { } amount) continue;
            var monthly = PerMonth(amount, schedule.Cycle, schedule.Interval);
            var converted = fx.ToBaseOn(monthly, schedule.Currency, today);
            if (converted is null) { missing.Add(FxSnapshot.Normalize(schedule.Currency)); continue; }
            income += converted.Value;
            lines.Add(new WealthPreviewLine(
                WealthPreviewLineKinds.Income, schedule.Id, schedule.Name, Round(converted.Value), false));
        }

        // ---- fixed costs: the contracts the owner marked as such ----
        var fixedCosts = 0m;
        var contracts = await db.Contracts.AsNoTracking()
            .Where(contract =>
                contract.FullWorthSpaceId == fullWorthSpaceId &&
                contract.IsActive &&
                contract.CountsAsFixedCost &&
                contract.MergedIntoContractId == null &&
                (contract.AccountId == null || visible.Contains(contract.AccountId.Value)))
            .Select(contract => new
            {
                contract.Id, contract.Name, contract.Amount, contract.Currency,
                contract.BillingCycle, contract.Interval
            })
            .ToListAsync(ct);

        foreach (var contract in contracts)
        {
            var monthly = PerMonth(Math.Abs(contract.Amount), contract.BillingCycle, contract.Interval);
            var converted = fx.ToBaseOn(monthly, contract.Currency, today);
            if (converted is null) { missing.Add(FxSnapshot.Normalize(contract.Currency)); continue; }
            fixedCosts += converted.Value;
            lines.Add(new WealthPreviewLine(
                WealthPreviewLineKinds.FixedCost, contract.Id, contract.Name, Round(converted.Value), false));
        }

        // ---- variable spend: what is actually spent BESIDES the contracts ----
        var linked = await LoadContractLinkedTransactionIdsAsync(db, fullWorthSpaceId, ct);
        var expenses = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                visible.Contains(transaction.AccountId) &&
                transaction.Amount < 0 &&
                !transaction.IsIgnored &&
                !transaction.IsTransfer &&
                (transaction.BookingDate ?? transaction.ValueDate) >= windowStart &&
                (transaction.BookingDate ?? transaction.ValueDate) < windowEnd)
            .Select(transaction => new
            {
                transaction.Id, transaction.Amount, transaction.Currency,
                transaction.BookingDate, transaction.ValueDate
            })
            .ToListAsync(ct);

        var variableTotal = 0m;
        foreach (var expense in expenses)
        {
            // A contract payment is already in the fixed costs above. Counting it here as well would
            // subtract the same rent twice, which is the one thing this figure must not do.
            if (linked.Contains(expense.Id)) continue;
            var date = expense.BookingDate ?? expense.ValueDate ?? today;
            var converted = fx.ToBaseOn(-expense.Amount, expense.Currency, date);
            if (converted is null) { missing.Add(FxSnapshot.Normalize(expense.Currency)); continue; }
            variableTotal += converted.Value;
        }

        var variable = Round(variableTotal / months);
        if (variable > 0m)
            lines.Add(new WealthPreviewLine(
                WealthPreviewLineKinds.VariableSpend, null, "variable", variable, true));

        // ---- budget limits: shown beside the average, never inside it ----
        var budgetLimit = 0m;
        var budgets = await db.Budgets.AsNoTracking()
            .Where(budget => budget.FullWorthSpaceId == fullWorthSpaceId && budget.IsActive)
            .Select(budget => new { budget.Amount, budget.Currency, budget.Period })
            .ToListAsync(ct);
        foreach (var budget in budgets)
        {
            var monthly = PerMonth(Math.Abs(budget.Amount), budget.Period, 1);
            var converted = fx.ToBaseOn(monthly, budget.Currency, today);
            if (converted is null) { missing.Add(FxSnapshot.Normalize(budget.Currency)); continue; }
            budgetLimit += converted.Value;
        }

        return new WealthPreviewBasisView(
            baseCurrency,
            lines,
            Round(income),
            Round(fixedCosts),
            variable,
            Round(budgetLimit),
            Round(income - fixedCosts - variable),
            months,
            missing.Count == 0,
            missing.Select(x => x.ToUpperInvariant()).ToArray());
    }

    /// <summary>
    /// A cycle to a monthly figure, through the same helper the contracts module uses for its own
    /// monthly equivalent — so a quarterly contract is worth the same third of a payment in the preview
    /// as it is in the contract list.
    /// </summary>
    private static decimal PerMonth(decimal amount, string? cycle, int interval) =>
        amount * ContractCycle.PeriodsPerYear(cycle, interval) / 12m;

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed record IncomeScheduleRow(Guid Id, string Name, decimal? Amount, string Currency, string Cycle, int Interval);

    private static async Task<List<IncomeScheduleRow>> LoadIncomeSchedulesAsync(
        FullWorthDbContext db, Guid space, HashSet<Guid> visible, CancellationToken ct)
    {
        var rows = new List<IncomeScheduleRow>();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT "Id","Name","ExpectedAmount","Currency","Cycle","Interval","AccountId"
FROM "IncomeSchedules"
WHERE "FullWorthSpaceId" = @space AND "IsActive"
""";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@space";
        parameter.Value = space;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var accountId = reader.IsDBNull(6) ? (Guid?)null : reader.GetGuid(6);
            // An income on an account the user cannot see is not theirs to plan with.
            if (accountId is { } id && !visible.Contains(id)) continue;
            rows.Add(new IncomeScheduleRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5)));
        }
        return rows;
    }

    /// <summary>
    /// The transactions a contract already accounts for. Raw SQL because <c>ContractTransactionLinks</c>
    /// has no CLR entity — it is written and read with SQL everywhere else too.
    /// </summary>
    private static async Task<HashSet<Guid>> LoadContractLinkedTransactionIdsAsync(
        FullWorthDbContext db, Guid space, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT DISTINCT "TransactionId" FROM "ContractTransactionLinks" WHERE "FullWorthSpaceId" = @space
""";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@space";
        parameter.Value = space;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }
}
