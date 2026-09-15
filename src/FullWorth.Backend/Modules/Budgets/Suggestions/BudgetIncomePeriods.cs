using FullWorth.Backend.Modules.Budgets.Cycles;

namespace FullWorth.Backend.Modules.Budgets.Suggestions;

/// <summary>
/// Periodengrenzen, die an einer wiederkehrenden Einnahme haengen statt am Kalender (#115).
///
/// "An Vertrag/Einnahme koppeln": eine erkannte Gehaltsbuchung beginnt die Periode, und sie laeuft bis
/// unmittelbar vor die naechste. Verschiebt sich das Gehalt wegen eines Wochenendes, verschiebt sich die
/// Grenze mit - genau das ist der Punkt gegenueber einem festen Stichtag.
///
/// Der Fall, der die Sache schwierig macht, ist die FEHLENDE Buchung. Sie darf die Periode nicht offen
/// lassen, sonst waechst das Budgetfenster still ins Unendliche und der Benutzer sieht wochenlang eine
/// Zahl, die niemand mehr einordnen kann. Aus den bisherigen Abstaenden wird deshalb ein erwarteter
/// naechster Termin abgeleitet und als Grenze verwendet, bis eine echte Buchung da ist.
///
/// Kein eigener Begriff "Gehaltstag" - gekoppelt werden kann an jede wiederkehrende Einnahme.
/// </summary>
public static class BudgetIncomePeriods
{
    /// <summary>
    /// Die Perioden aus den Einnahmebuchungen, aelteste zuerst. Die letzte laeuft bis zum erwarteten
    /// naechsten Termin (minus ein Tag) - oder bis <paramref name="today"/>, falls der schon vorbei ist.
    /// </summary>
    /// <param name="incomeDates">Die Tage, an denen die gekoppelte Einnahme gebucht wurde.</param>
    /// <param name="today">Der Stichtag.</param>
    public static IReadOnlyList<BudgetCyclePeriod> Build(IReadOnlyList<DateOnly> incomeDates, DateOnly today)
    {
        var dates = incomeDates.Where(date => date <= today).Distinct().OrderBy(date => date).ToList();
        if (dates.Count == 0) return [];

        var periods = new List<BudgetCyclePeriod>();
        for (var index = 0; index < dates.Count - 1; index++)
            periods.Add(new BudgetCyclePeriod(dates[index], dates[index + 1].AddDays(-1)));

        var last = dates[^1];
        var expected = ExpectedNext(dates);
        // Der erwartete Termin liegt schon hinter uns und es kam nichts: die Periode endet nicht dort,
        // sondern laeuft bis heute weiter. Sonst stuende der Benutzer in einer Periode, die es laut
        // Rechnung nicht mehr gibt.
        var end = expected.HasValue && expected.Value > today ? expected.Value.AddDays(-1) : today;
        if (end < last) end = last;
        periods.Add(new BudgetCyclePeriod(last, end));
        return periods;
    }

    /// <summary>
    /// Wann die naechste Einnahme zu erwarten ist: der letzte Termin plus der typische Abstand. Typisch
    /// heisst Median, nicht Mittelwert - ein einzelner Nachzahlungsmonat soll den Rhythmus nicht kippen.
    /// Null, wenn es nur einen Termin gibt und damit noch keinen Abstand.
    /// </summary>
    public static DateOnly? ExpectedNext(IReadOnlyList<DateOnly> incomeDates)
    {
        var dates = incomeDates.Distinct().OrderBy(date => date).ToList();
        if (dates.Count < 2) return null;

        var gaps = new List<int>();
        for (var index = 1; index < dates.Count; index++)
            gaps.Add(dates[index].DayNumber - dates[index - 1].DayNumber);
        gaps.Sort();
        var middle = gaps.Count / 2;
        var typical = gaps.Count % 2 == 1 ? gaps[middle] : (gaps[middle - 1] + gaps[middle]) / 2;
        return dates[^1].AddDays(Math.Max(1, typical));
    }

    /// <summary>Die Periode, in der <paramref name="date"/> liegt - oder nichts, wenn sie davor liegt.</summary>
    public static BudgetCyclePeriod? PeriodOf(IReadOnlyList<BudgetCyclePeriod> periods, DateOnly date)
    {
        foreach (var period in periods)
            if (period.Contains(date)) return period;
        return null;
    }
}
