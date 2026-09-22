using FullWorth.Backend.Modules.Budgets.Cycles;

namespace FullWorth.Backend.Modules.Budgets.CarryOver;

/// <summary>
/// Welche abgeschlossenen Perioden in den Uebertrag eingehen - und ab wann ueberhaupt gerechnet wird.
///
/// <see cref="BudgetCarryOverCalculator"/> rechnet aus einer Liste vergangener Ausgaben; woher diese
/// Liste kommt, steht hier. Beides zusammen ist der Uebertrag aus #115.
///
/// Diese Datei existiert, weil es die Angaben zweimal brauchte und einmal fehlte. Der Speicher rechnete
/// den Uebertrag, aber die Route, die ihn ausliefert, wird von
/// <see cref="BudgetReconciliationService"/> bedient - und der kannte ihn nicht. Ein Budget mit
/// Uebertrag zeigte deshalb ueberall den nackten Grundbetrag. Zwei Rechenwege waeren die naechste
/// Stelle gewesen, an der die beiden Zahlen wieder auseinanderlaufen.
/// </summary>
public static class BudgetCarryOverWindow
{
    /// <summary>Ein Budget ohne Uebertrag; mit Uebertrag entscheidet, ob auch eine Ueberziehung mitgeht.</summary>
    public static CarryOverMode Mode(Budget budget) =>
        !budget.CarryOver
            ? CarryOverMode.Disabled
            : budget.CarryOverOverspend ? CarryOverMode.Enabled : CarryOverMode.PositiveOnly;

    /// <summary>
    /// Ab wann der Uebertrag zaehlt. Das ist NICHT der Periodenbeginn: die Periode sagt, wie lang ein
    /// Fenster ist, diese Angabe sagt, ab welchem Fenster ueberhaupt gerechnet wird.
    ///
    /// Ohne Angabe bleibt es beim bisherigen Verhalten - ab dem Startdatum, sonst ab der Anlage.
    /// </summary>
    /// <param name="reference">
    /// Der Tag, auf den geschaut wird. Er zaehlt und nicht "heute": wer eine vergangene Periode
    /// abfragt, will deren Uebertrag sehen und nicht den der laufenden.
    /// </param>
    public static DateOnly ActiveFrom(Budget budget, BudgetCycleDefinition cycle, DateOnly reference)
    {
        switch (budget.CarryOverStart)
        {
            // Nur diese Periode: aeltere Historie beeinflusst den Uebertrag nicht.
            case "this-period": return BudgetCycleCalculator.CurrentPeriod(cycle, reference).Start;
            // Ein Datum mitten in einer Periode meint die ganze Periode - ein halber Uebertrag kommt
            // in keiner Abrechnung vor.
            case "from-date" when budget.CarryOverFrom is { } chosen:
                return BudgetCycleCalculator.CurrentPeriod(cycle, chosen).Start;
        }
        if (budget.StartDate is { } explicitStart) return explicitStart;
        var created = DateOnly.FromDateTime(budget.CreatedAt.UtcDateTime);
        return BudgetCycleCalculator.CurrentPeriod(cycle, created).Start;
    }

    /// <summary>Jede abgeschlossene Periode zwischen <paramref name="activeFrom"/> und der laufenden, aelteste zuerst.</summary>
    public static List<BudgetCyclePeriod> PriorPeriods(
        BudgetCycleDefinition cycle,
        DateOnly activeFrom,
        BudgetCyclePeriod current)
    {
        if (activeFrom >= current.Start) return [];

        var periods = new List<BudgetCyclePeriod>();
        var cursor = BudgetCycleCalculator.CurrentPeriod(cycle, activeFrom);
        var guard = 0;
        while (cursor.Start < current.Start && guard++ < 20000)
        {
            periods.Add(cursor);
            cursor = BudgetCycleCalculator.CurrentPeriod(cycle, cursor.EndExclusive);
        }
        return periods;
    }

    /// <summary>
    /// Der Verbrauch in Prozent - bezogen auf das EFFEKTIVE Budget, also einschliesslich Uebertrag.
    /// Ein Budget, das durch eine alte Ueberziehung bei null oder darunter liegt, gibt 101 zurueck:
    /// eine Division waere hier entweder ein Fehler oder eine erfundene Zahl.
    /// </summary>
    public static decimal PercentUsed(decimal effectiveBudget, decimal spent)
    {
        if (effectiveBudget > 0m)
            return Math.Round(spent / effectiveBudget * 100m, 2, MidpointRounding.AwayFromZero);
        return spent > 0m || effectiveBudget < 0m ? 101m : 0m;
    }
}
