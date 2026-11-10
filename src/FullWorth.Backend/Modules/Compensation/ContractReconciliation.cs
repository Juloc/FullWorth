using System.Globalization;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Vertragsstaende gegen Abrechnungen: stimmt das Brutto, das der Vertrag verspricht, mit dem ueberein,
/// das die Abrechnung zeigt?
///
/// Verglichen wird das laufende Monatsbrutto - das Gesamtbrutto ohne Bonus und ohne den Sachbezug des
/// Dienstwagens, denn beides steht nicht im Vertragsgehalt. Ein Monat ohne Vertragsstand (vor dem ersten
/// "gueltig ab") wird nicht verglichen: da gibt es nichts, wogegen.
///
/// Zwei Befunde. Weicht ein einzelner Monat ab, ist das ein Hinweis ("weicht ab"). Haelt sich dasselbe
/// abweichende Brutto ueber mindestens zwei Monate oder bis zur letzten Abrechnung, hat sich das Gehalt
/// offenbar geaendert, ohne dass ein Nachtrag da ist: "Aenderung erkannt", mit dem Vorschlag, den Stand
/// ab dem ersten dieser Monate anzulegen.
/// </summary>
public static class ContractReconciliation
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private const decimal Tolerance = 1m;
    private const int MaxHints = 3;

    public static IReadOnlyList<ContractHint> Reconcile(IReadOnlyList<ContractStateView> states, IReadOnlyList<PayslipRecordView> payslips)
    {
        if (states.Count == 0 || payslips.Count == 0) return [];
        var ordered = states.OrderBy(x => x.State.ValidFrom).ToList();

        var months = payslips
            .Select(x => x.Payslip)
            .GroupBy(x => (x.Period.Year, x.Period.Month))
            .Select(group => group.First())
            .OrderBy(x => x.Period)
            .Select(p => (Month: new DateOnly(p.Period.Year, p.Period.Month, 1), Running: p.GrossPay - p.Bonus - p.CompanyCarTaxableBenefit,
                Contract: ordered.LastOrDefault(s => s.State.ValidFrom <= new DateOnly(p.Period.Year, p.Period.Month, DateTime.DaysInMonth(p.Period.Year, p.Period.Month)))?.State.GrossMonthly))
            .Where(x => x.Contract is not null)
            .Select(x => (x.Month, x.Running, Contract: x.Contract!.Value))
            .ToList();

        var hints = new List<ContractHint>();
        for (var i = 0; i < months.Count;)
        {
            var (month, running, contract) = months[i];
            if (Math.Abs(running - contract) <= Tolerance) { i++; continue; }

            // Die Folge gleich abweichender Monate mit demselben Vertragsstand.
            var end = i;
            while (end + 1 < months.Count && months[end + 1].Contract == contract
                   && Math.Abs(months[end + 1].Running - running) <= Tolerance
                   && months[end + 1].Month == months[end].Month.AddMonths(1)) end++;
            var lastRun = end == months.Count - 1;

            if (end > i || lastRun)
                hints.Add(new("change_detected",
                    $"Änderung erkannt: seit {Label(month)} {Money(running)} Brutto statt {Money(contract)} laut Vertrag - es gibt keinen Nachtrag dazu.",
                    month, running));
            else
                hints.Add(new("differs", $"Das Brutto im {Label(month)} ({Money(running)}) weicht vom Vertrag ({Money(contract)}) ab.", null, null));
            i = end + 1;
        }
        return hints.AsEnumerable().Reverse().Take(MaxHints).ToList();
    }

    private static string Label(DateOnly month) => month.ToString("MMMM yyyy", German);
    private static string Money(decimal value) => value.ToString("N2", German) + " €";
}
