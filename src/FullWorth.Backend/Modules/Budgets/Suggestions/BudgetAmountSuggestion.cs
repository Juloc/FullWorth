namespace FullWorth.Backend.Modules.Budgets.Suggestions;

/// <summary>Was der Vorschlag sagt und woraus er entstanden ist.</summary>
/// <param name="Average">Der Durchschnitt der beruecksichtigten abgeschlossenen Perioden.</param>
/// <param name="Suggested">Der gerundete Vorschlag - das, was im Feld steht.</param>
/// <param name="PeriodsUsed">Wie viele Perioden eingeflossen sind.</param>
/// <param name="OutliersDamped">Wie viele Perioden als Ausreisser gedaempft wurden.</param>
public sealed record BudgetAmountProposal(
    decimal Average, decimal Suggested, int PeriodsUsed, int OutliersDamped);

/// <summary>
/// Die vorgeschlagene Budgethoehe aus den abgeschlossenen Perioden (#115).
///
/// Vier Dinge, die der Vorschlag koennen muss und die jeweils einen Grund haben:
///
/// - <b>Nur abgeschlossene Perioden.</b> Die laufende ist halb vorbei; sie mitzuzaehlen macht jeden
///   Vorschlag zu niedrig. Die Auswahl der Perioden trifft <see cref="BudgetCadenceInference"/>.
/// - <b>Neuere zaehlen mehr.</b> Wer vor einem Jahr anders gelebt hat, soll das Budget von heute nicht
///   bestimmen - aber die alte Periode auch nicht wegwerfen, solange sie die einzige Historie ist.
/// - <b>Ausreisser daempfen, nicht loeschen.</b> Ein einzelner Grosseinkauf ist echtes Geld. Er wird auf
///   das Doppelte des Medians begrenzt statt gestrichen: der Vorschlag bleibt bezahlbar, ohne dass die
///   Ausgabe verschwindet.
/// - <b>Sinnvoll runden.</b> 92,18 EUR ist kein Budget, 95 EUR ist eines.
///
/// Alles in <c>decimal</c>. Ein Budgetvorschlag ist Geld.
/// </summary>
public static class BudgetAmountSuggestion
{
    /// <summary>Wie viele Perioden hoechstens einfliessen - je kuerzer das Intervall, desto mehr.</summary>
    public static int HistoryWindow(BudgetCadence cadence) => cadence switch
    {
        BudgetCadence.Weekly => 12,
        BudgetCadence.Monthly => 6,
        BudgetCadence.Quarterly => 4,
        _ => 3
    };

    /// <summary>
    /// Der Vorschlag aus den Periodensummen, aelteste zuerst. Eine leere Historie ergibt einen
    /// Vorschlag von 0 - die Oberflaeche zeigt ihn als aenderbares Feld, nicht als Wahrheit.
    /// </summary>
    public static BudgetAmountProposal Propose(IReadOnlyList<decimal> periodSums, BudgetCadence cadence)
    {
        var used = periodSums.TakeLast(HistoryWindow(cadence)).Select(Math.Abs).ToList();
        if (used.Count == 0) return new BudgetAmountProposal(0m, 0m, 0, 0);

        var cap = Cap(used);
        var damped = 0;
        var weightedSum = 0m;
        var weightTotal = 0m;
        for (var index = 0; index < used.Count; index++)
        {
            var value = used[index];
            if (cap > 0m && value > cap) { value = cap; damped++; }
            // Lineare Gewichtung: die juengste Periode zaehlt so oft, wie es Perioden gibt, die aelteste
            // einmal. Kein Exponent - der wuerde bei zwoelf Wochen die aeltesten faktisch loeschen.
            var weight = index + 1;
            weightedSum += value * weight;
            weightTotal += weight;
        }

        var average = Math.Round(weightedSum / weightTotal, 2, MidpointRounding.AwayFromZero);
        return new BudgetAmountProposal(average, RoundUpNicely(average), used.Count, damped);
    }

    /// <summary>Die Obergrenze fuer eine einzelne Periode: das Doppelte des Medians.</summary>
    private static decimal Cap(IReadOnlyList<decimal> values)
    {
        if (values.Count < 3) return 0m;   // zu wenig, um zu wissen, was normal ist
        var sorted = values.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;
        var median = sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2m;
        return median * 2m;
    }

    /// <summary>
    /// Auf eine Zahl runden, die ein Mensch als Budget hinschreiben wuerde: 92,18 wird 95, 1 043 wird
    /// 1 100. Immer AUFwaerts - ein Budget, das unter dem Durchschnitt liegt, ist von vornherein
    /// gerissen.
    /// </summary>
    public static decimal RoundUpNicely(decimal value)
    {
        if (value <= 0m) return 0m;
        var step = value switch
        {
            < 20m => 1m,
            < 100m => 5m,
            < 500m => 10m,
            < 2000m => 50m,
            < 10000m => 100m,
            _ => 500m
        };
        return Math.Ceiling(value / step) * step;
    }
}
