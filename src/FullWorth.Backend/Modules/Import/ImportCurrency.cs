namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Wann zwei Waehrungsangaben einander widersprechen - und wann eine gar keine ist (#112).
///
/// <c>XXX</c> ist der ISO-4217-Code fuer "keine Waehrung". Ein PayPal-Wallet meldet ihn, weil es
/// mehrere Waehrungen zugleich haelt: der Code sagt "hier steht keine", nicht "hier steht eine
/// andere".
///
/// Der Importweg pruefte an vier Stellen auf GLEICHHEIT. Damit wurde aus der fehlenden Angabe ein
/// Widerspruch: das PayPal-Konto erschien als "PayPal · XXX", ausgegraut, und liess sich mit keinem
/// Import verknuepfen. Schlimmer noch - eine bereits bestaetigte Verknuepfung wurde beim naechsten
/// Import stillschweigend wieder verworfen, obwohl sie ausdruecklich als massgeblich gilt.
///
/// Die Sicherung, um die es wirklich geht, bleibt unangetastet: Euro-Buchungen landen nicht auf einem
/// Dollar-Konto. Nur ein Konto, das gar keine Waehrung erklaert, widerspricht niemandem.
///
/// Die Regel steht hier und nicht in den Diensten, weil sie in beiden dieselbe sein muss. Sie war es
/// nicht: vier Vergleiche, vier Gelegenheiten, einen davon zu vergessen.
/// </summary>
internal static class ImportCurrency
{
    private const string NoCurrency = "XXX";

    /// <summary>Ob die Angabe eine Waehrung IST - dreistellig und nicht <c>XXX</c>.</summary>
    public static bool IsDeclared(string? currency)
        => !string.IsNullOrWhiteSpace(currency)
           && currency.Trim().Length == 3
           && !string.Equals(currency.Trim(), NoCurrency, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ob die beiden einander widersprechen: nur wenn BEIDE eine Waehrung nennen und die sich
    /// unterscheidet.
    /// </summary>
    public static bool Conflict(string? left, string? right)
        => IsDeclared(left) && IsDeclared(right)
           && !string.Equals(left!.Trim(), right!.Trim(), StringComparison.OrdinalIgnoreCase);
}
