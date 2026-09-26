using FullWorth.Backend.Validation;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Der Kontostand eines Kontos aus dem Finanzguru-Export (#131).
///
/// Der Export "Alle Buchungen" traegt in der Spalte <c>Kontostand</c> den Stand nach jeder Buchung. Er
/// wurde bisher verworfen - ein importiertes Konto hatte deshalb keinen Wert, fehlte im Vermoegen und
/// bat um "Kontostand ergaenzen", obwohl die Datei ihn kennt.
///
/// Wie bei jedem Import wird er <b>nachgerechnet, nicht geglaubt</b>: der Stand nach der juengsten
/// Buchung muss der Stand davor plus deren Betrag sein. Stimmt das nicht, ist mindestens eine der beiden
/// Zahlen falsch gelesen oder eine Buchung fehlt - und ein falscher Kontostand ist schlimmer als keiner.
/// Buchungen mit einem Datum nach heute (vorgemerkte Umsaetze, die Finanzguru schon zeigt) tragen einen
/// Stand, den das Konto noch nicht hat; sie zaehlen fuer den Anker nicht.
/// </summary>
internal static class FinanzguruBalance
{
    /// <summary>
    /// Der Stand nach der juengsten Buchung bis <paramref name="today"/>, mit ihrem Datum - oder
    /// <c>null</c>, wenn die Datei ihn nicht nennt oder er sich nicht nachrechnen laesst.
    /// </summary>
    /// <param name="accountRows">Die Elternzeilen EINES Quellkontos (keine Aufteilungen).</param>
    internal static StatementBalance? Anchor(IReadOnlyList<FinanzguruRow> accountRows, DateOnly today)
    {
        // Der Export steht neueste zuerst; innerhalb eines Tages entscheidet die Reihenfolge der Datei.
        var ordered = accountRows
            .Where(row => row.BookingDate <= today)
            .Select(row => (Row: row, Balance: BalanceOf(row)))
            .Where(entry => entry.Balance is not null)
            .OrderByDescending(entry => entry.Row.BookingDate)
            .ThenBy(entry => entry.Row.RowNumber)
            .ToList();
        if (ordered.Count == 0) return null;

        var (newest, balance) = ordered[0];
        // Mit einer einzigen Buchung gibt es nichts, wogegen sich der Stand pruefen liesse - aber auch
        // nichts, was ihm widerspricht.
        if (ordered.Count > 1 && balance!.Value != ordered[1].Balance!.Value + newest.Amount) return null;

        return new StatementBalance(balance!.Value, newest.Currency, newest.BookingDate);
    }

    private static decimal? BalanceOf(FinanzguruRow row) =>
        row.RawValues.TryGetValue("Kontostand", out var text) && !string.IsNullOrWhiteSpace(text)
            ? ImportNumber.TryParse(text, ImportNumber.ThreeDigitTail.Grouping)
            : null;
}
