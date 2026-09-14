using FullWorth.Backend.Security;

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
public static class WealthPreviewBasisEndpoints
{
    public static IEndpointRouteBuilder MapWealthPreviewBasisEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/wealth/preview-basis", async (
            Guid fullWorthSpaceId,
            int? months,
            CurrentUserContext currentUser,
            SpaceAccess space,
            WealthPreviewBasisService basisService,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

            var basis = await basisService.BuildAsync(userId, fullWorthSpaceId, months, ct);
            return basis is null ? Results.NotFound() : Results.Ok(basis);
        });

        return app;
    }
}
