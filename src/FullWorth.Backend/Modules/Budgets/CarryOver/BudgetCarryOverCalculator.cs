namespace FullWorth.Backend.Modules.Budgets.CarryOver;

/// <summary>Whether a budget rolls its unspent (or overspent) balance into the next cycle.</summary>
public enum CarryOverMode
{
    /// <summary>Every cycle starts fresh at the base budget amount.</summary>
    Disabled,

    /// <summary>Unused budget rolls forward, but an overspend never reduces the next cycle.</summary>
    PositiveOnly,

    /// <summary>The remainder (leftover or overspend) of each cycle is carried into the next.</summary>
    Enabled,
}

/// <summary>
/// Computes budget carry-over deterministically from a base amount and the spend history of the
/// preceding cycles. Pure and side-effect free: it never rewrites historical transactions — the
/// per-cycle spends are supplied by the caller and only the running balance is derived.
/// </summary>
public static class BudgetCarryOverCalculator
{
    /// <summary>
    /// The balance carried into the current cycle. Positive means leftover budget rolls forward;
    /// negative means a prior overspend is still being paid back. Always 0 when carry-over is
    /// disabled or there is no prior history.
    /// </summary>
    /// <param name="mode">Whether carry-over applies.</param>
    /// <param name="baseAmount">The budget's per-cycle base amount.</param>
    /// <param name="priorSpends">Spend for each preceding cycle, oldest first, from the first cycle the budget applied.</param>
    public static decimal CarriedIn(CarryOverMode mode, decimal baseAmount, IReadOnlyList<decimal> priorSpends)
    {
        ArgumentNullException.ThrowIfNull(priorSpends);
        if (mode == CarryOverMode.Disabled) return 0m;

        var carry = 0m;
        foreach (var spend in priorSpends)
        {
            // Each cycle's effective budget is the base plus whatever rolled in; its leftover
            // (which can be negative for an overspend) becomes the next cycle's carry-in.
            var effective = baseAmount + carry;
            carry = effective - spend;
            if (mode == CarryOverMode.PositiveOnly && carry < 0m)
                carry = 0m;
        }

        return carry;
    }

    /// <summary>The effective budget for the current cycle: base amount plus any carried-in balance.</summary>
    public static decimal EffectiveBudget(CarryOverMode mode, decimal baseAmount, IReadOnlyList<decimal> priorSpends) =>
        baseAmount + CarriedIn(mode, baseAmount, priorSpends);
}
