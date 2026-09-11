namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// The bAV projection arithmetic (docs/PENSION.md step 3). Pure, static-in-spirit and free of any
/// dependency, so every number it produces can be checked by hand in a unit test and none of them can
/// reach a row: a projection that could be persisted would be indistinguishable from a guarantee, and
/// that is the one thing this feature must never do.
///
/// Three conventions are fixed here. Each one is a choice, so each one is written down — a projection
/// whose conventions are undocumented cannot be checked by anyone later:
///
/// <list type="number">
///   <item><b>Compounding is the twelfth root of the annual factor</b>, never <c>annual / 12</c>. See
///     <see cref="TwelfthRoot"/> for what the shortcut costs.</item>
///   <item><b>A contribution is paid at the start of the month</b> and earns that whole month, because
///     that is when a monthly bAV premium is actually debited. See <see cref="Project"/>.</item>
///   <item><b>An annual cost percentage is subtracted from the annual return in percentage points.</b>
///     See <see cref="MonthlyFactor"/>: for Effektivkosten that is not an approximation, it is the
///     definition of the figure.</item>
/// </list>
///
/// It deliberately does <b>not</b> round. Money is rounded once, at the edge where it is shown or
/// summed (<c>PensionProjectionStore</c>), because rounding every contract to the cent and then adding
/// the results up makes a total that no longer equals the projection of the same money — and the
/// comparison rests on exactly that equality.
/// </summary>
public sealed class PensionProjectionCalculator : IBavProjectionCalculator
{
    /// <summary>
    /// At −100 % a year there is nothing left, and below it the annual factor is negative and has no
    /// real twelfth root. The floor is a refusal to produce a number, not a cap on a plausible input:
    /// the store never lets a return past ±100 %.
    /// </summary>
    private const decimal RuinAnnualPercent = -100m;

    /// <inheritdoc />
    public decimal Project(
        decimal balance,
        decimal monthlyContribution,
        int months,
        decimal annualPercent,
        decimal annualCostPercent)
    {
        // No time passed, so nothing happened. The balance is returned exactly as it came in — even a
        // rounding here would be an edit of a fact the caller handed over.
        if (months <= 0) return balance;

        var factor = MonthlyFactor(annualPercent, annualCostPercent);
        var capital = balance;
        for (var month = 0; month < months; month++)
            // Start of the month: the premium is debited first and then earns the full month. Posting
            // it at the end instead would quietly take one month of growth off every single payment,
            // which over a 30-year contract is a visible amount and would be invisible in the code.
            capital = (capital + monthlyContribution) * factor;

        return capital;
    }

    /// <summary>
    /// The factor one month of the contract multiplies the capital by.
    ///
    /// The cost is subtracted from the return in percentage points because that is what the figure the
    /// projection prefers actually is: Effektivkosten (reduction in yield) is defined as the
    /// percentage points a contract's costs take off its yield per year. A TER or a
    /// Verwaltungskostensatz on capital behaves the same way. Subtracting before taking the root, and
    /// not after, keeps "7 % gross with 1 % costs" identical to a flat 6 % assumption — which is the
    /// only reading a user can check against their own statement.
    /// </summary>
    public static decimal MonthlyFactor(decimal annualPercent, decimal annualCostPercent)
    {
        var netAnnualPercent = annualPercent - annualCostPercent;
        if (netAnnualPercent <= RuinAnnualPercent) return 0m;

        return TwelfthRoot(1m + netAnnualPercent / 100m);
    }

    /// <summary>
    /// The twelfth root of an annual growth factor, i.e. the monthly rate that compounds to it.
    ///
    /// <c>annual / 12</c> is the tempting shortcut and it is wrong in the user's favour: twelve months
    /// of 7 %/12 compound to 7.229 %, so a 7 % assumption silently becomes 7.23 % — about 0.23
    /// percentage points a year that exist on screen and not in the account, and after thirty years
    /// that is roughly 7 % more capital than the contract will ever hold.
    ///
    /// <c>decimal</c> has no root, so the seed goes through <c>double</c>: this is the only place in
    /// the file that leaves decimal, and it is a seed and not the answer. A double carries about 16
    /// significant digits and the cast back to decimal keeps 15, which is plenty for a cent but would
    /// drift a little over 360 compounding steps — so two Newton steps, done in decimal, pull the
    /// root back to decimal precision before anything is compounded with it.
    /// </summary>
    public static decimal TwelfthRoot(decimal annualFactor)
    {
        // Total loss (factor 0) is a number; a negative factor is not a contract.
        if (annualFactor <= 0m) return 0m;

        var root = (decimal)Math.Pow((double)annualFactor, 1d / 12d);

        // Newton on x¹² = a, written as a relative correction because the root sits just above 1 and
        // the correction is then the small quantity: x ← x · (1 + (a/x¹² − 1)/12).
        for (var step = 0; step < 2; step++)
        {
            var power = Power12(root);
            if (power <= 0m) break;
            root *= 1m + (annualFactor / power - 1m) / 12m;
        }

        return root;
    }

    /// <summary>
    /// Whole months between two dates. A part month is not a month of growth: rounding it up would
    /// invent interest for a period that has not happened, and this arithmetic may not invent anything.
    /// </summary>
    public static int MonthsBetween(DateOnly from, DateOnly to)
    {
        if (to <= from) return 0;
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        if (to.Day < from.Day) months--;
        return months < 0 ? 0 : months;
    }

    private static decimal Power12(decimal value)
    {
        var square = value * value;
        var fourth = square * square;
        return fourth * fourth * fourth;
    }
}
