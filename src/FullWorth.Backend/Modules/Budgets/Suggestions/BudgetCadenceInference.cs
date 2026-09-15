using FullWorth.Backend.Modules.Budgets.Cycles;

namespace FullWorth.Backend.Modules.Budgets.Suggestions;

/// <summary>Eine Buchung, soweit der Vorschlag sie braucht: wann und wie viel.</summary>
public readonly record struct BudgetHistoryEntry(DateOnly Date, decimal Amount);

/// <summary>Ein Intervallvorschlag mit seiner Begruendung.</summary>
/// <param name="Cadence">Woche, Monat, Quartal oder Jahr.</param>
/// <param name="Confidence">0 bis 1. Wie gut die Historie zu diesem Intervall passt.</param>
/// <param name="CompletePeriods">Wie viele abgeschlossene Perioden die Historie hergibt.</param>
/// <param name="ActiveShare">Anteil der abgeschlossenen Perioden, in denen ueberhaupt etwas gebucht wurde.</param>
/// <param name="Variation">Streuung der Periodensummen, 0 = alle gleich.</param>
public sealed record BudgetCadenceCandidate(
    BudgetCadence Cadence,
    decimal Confidence,
    int CompletePeriods,
    decimal ActiveShare,
    decimal Variation);

/// <summary>Die vier Grundintervalle, die ein Budget haben kann.</summary>
public enum BudgetCadence { Weekly, Monthly, Quarterly, Yearly }

/// <summary>
/// Welches Intervall zu den tatsaechlichen Buchungen passt (#115).
///
/// Es gibt hier keine Kategorieregel. "Lebensmittel heisst woechentlich" war genau das, was das Issue
/// verbietet: es stimmt fuer den einen und nicht fuer den naechsten, und niemand kann es widerlegen,
/// weil die Regel nicht aus Daten kommt. Was hier zaehlt, ist ausschliesslich, wie die Buchungen des
/// Benutzers ueber die Zeit liegen.
///
/// Bewertet wird je Kandidat:
///
/// - wie viele abgeschlossene Perioden die Historie hergibt (eine einzige beweist nichts),
/// - in wie vielen davon ueberhaupt etwas gebucht wurde (ein Intervall, das die Haelfte der Perioden
///   leer laesst, ist zu kurz),
/// - wie stark die Periodensummen schwanken (je gleichmaessiger, desto eher ist es der Rhythmus),
/// - wie aktuell die Daten sind (eine Historie, die vor einem Jahr endet, sagt wenig ueber heute).
///
/// Es gibt IMMER einen Vorschlag. Reicht die Datenlage nicht, kommt <see cref="BudgetCadence.Monthly"/>
/// mit niedriger Sicherheit heraus - als Rueckfall des Verfahrens, nicht als Regel ueber eine Kategorie.
/// Die Oberflaeche zeigt die Sicherheit an, damit eine schwache Grundlage sichtbar bleibt.
///
/// Gerechnet wird durchgehend in <c>decimal</c>: Geld kommt hier vor, und ein Vorschlag, der auf
/// Fliesskomma beruht, ist bei jedem zweiten Aufruf ein anderer.
/// </summary>
public static class BudgetCadenceInference
{
    /// <summary>Unterhalb dieser Sicherheit sagt die Oberflaeche, dass die Datenlage duenn ist.</summary>
    public const decimal WeakConfidence = 0.45m;

    private static readonly BudgetCadence[] All = [BudgetCadence.Weekly, BudgetCadence.Monthly, BudgetCadence.Quarterly, BudgetCadence.Yearly];

    /// <summary>Alle Kandidaten, der beste zuerst.</summary>
    public static IReadOnlyList<BudgetCadenceCandidate> Rank(
        IReadOnlyList<BudgetHistoryEntry> history, DateOnly today)
    {
        var entries = history.Where(entry => entry.Date <= today).OrderBy(entry => entry.Date).ToList();
        if (entries.Count == 0)
            return [new BudgetCadenceCandidate(BudgetCadence.Monthly, 0m, 0, 0m, 0m)];

        var candidates = All.Select(cadence => Score(cadence, entries, today)).ToList();
        return [.. candidates.OrderByDescending(candidate => candidate.Confidence)
            // Bei Gleichstand das kuerzere Intervall: es ist das, das der Benutzer haeufiger sieht und
            // leichter korrigiert - ein zu langes faellt erst nach Monaten auf.
            .ThenBy(candidate => (int)candidate.Cadence)];
    }

    /// <summary>Der beste Kandidat. Ohne brauchbare Historie ist das monatlich mit Sicherheit 0.</summary>
    public static BudgetCadenceCandidate Best(IReadOnlyList<BudgetHistoryEntry> history, DateOnly today) =>
        Rank(history, today)[0];

    private static BudgetCadenceCandidate Score(
        BudgetCadence cadence, IReadOnlyList<BudgetHistoryEntry> entries, DateOnly today)
    {
        var first = entries[0].Date;
        var periods = CompletePeriods(cadence, first, today);
        if (periods.Count == 0)
            return new BudgetCadenceCandidate(cadence, 0m, 0, 0m, 0m);

        var sums = periods
            .Select(period => entries
                .Where(entry => entry.Date >= period.Start && entry.Date <= period.End)
                .Sum(entry => Math.Abs(entry.Amount)))
            .ToList();

        var active = sums.Count(sum => sum > 0m);
        var activeShare = (decimal)active / sums.Count;
        var variation = Variation(sums.Where(sum => sum > 0m).ToList());

        // Genug Perioden, um ueberhaupt von einem Rhythmus zu sprechen: bei einer Woche sind drei wenig,
        // bei einem Jahr sind drei schon viel. Deshalb je Intervall ein eigenes Ziel.
        var wanted = cadence switch
        {
            BudgetCadence.Weekly => 8,
            BudgetCadence.Monthly => 4,
            BudgetCadence.Quarterly => 3,
            _ => 2
        };
        var coverage = Math.Min(1m, (decimal)periods.Count / wanted);

        // Wie alt die juengste Buchung gemessen an der Periodenlaenge ist. Wer seit drei Perioden nichts
        // mehr gebucht hat, hat diesen Rhythmus nicht mehr.
        var lastDate = entries[^1].Date;
        var periodDays = Math.Max(1, periods[^1].LengthInDays);
        var staleness = (decimal)(today.DayNumber - lastDate.DayNumber) / periodDays;
        var recency = staleness <= 1m ? 1m : staleness >= 4m ? 0m : (4m - staleness) / 3m;

        var steadiness = 1m - Math.Min(1m, variation);
        var confidence = Round(0.35m * activeShare + 0.30m * steadiness + 0.20m * coverage + 0.15m * recency);
        return new BudgetCadenceCandidate(cadence, confidence, periods.Count, Round(activeShare), Round(variation));
    }

    /// <summary>
    /// Die abgeschlossenen Perioden zwischen der ersten Buchung und heute. Die LAUFENDE Periode ist
    /// nicht dabei: sie ist unvollstaendig, und sie wie eine abgeschlossene zu zaehlen zieht jeden
    /// Durchschnitt nach unten (#115).
    /// </summary>
    public static IReadOnlyList<BudgetCyclePeriod> CompletePeriods(
        BudgetCadence cadence, DateOnly from, DateOnly today)
    {
        var definition = Definition(cadence, from);
        var periods = new List<BudgetCyclePeriod>();
        var cursor = BudgetCycleCalculator.CurrentPeriod(definition, from);
        var current = BudgetCycleCalculator.CurrentPeriod(definition, today);
        // Eine Obergrenze, damit ein absurdes Startdatum keine Endlosschleife baut.
        for (var guard = 0; cursor.Start < current.Start && guard < 600; guard++)
        {
            periods.Add(cursor);
            cursor = BudgetCycleCalculator.NextPeriod(definition, cursor.Start);
        }
        return periods;
    }

    /// <summary>Der Zyklus zu einem Intervall. Woche und Jahr haengen am Startdatum, Monat und Quartal am Kalender.</summary>
    public static BudgetCycleDefinition Definition(BudgetCadence cadence, DateOnly anchor) => cadence switch
    {
        BudgetCadence.Weekly => BudgetCycleDefinition.Custom(anchor, 7),
        BudgetCadence.Monthly => BudgetCycleDefinition.CalendarMonth(),
        BudgetCadence.Quarterly => BudgetCycleDefinition.CalendarQuarter(),
        _ => BudgetCycleDefinition.CalendarYear()
    };

    /// <summary>Variationskoeffizient: Streuung im Verhaeltnis zum Mittel. 0 heisst, alle Perioden sind gleich.</summary>
    private static decimal Variation(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2) return 0m;
        var mean = values.Sum() / values.Count;
        if (mean == 0m) return 0m;
        // Mittlere absolute Abweichung statt Standardabweichung: sie braucht keine Wurzel und bleibt
        // damit exakt in decimal - und sie reagiert weniger heftig auf einen einzelnen Ausreisser.
        var deviation = values.Sum(value => Math.Abs(value - mean)) / values.Count;
        return deviation / mean;
    }

    private static decimal Round(decimal value) => Math.Round(Math.Clamp(value, 0m, 1m), 4);
}
