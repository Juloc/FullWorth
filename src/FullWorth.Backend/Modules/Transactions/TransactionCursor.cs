namespace FullWorth.Backend.Modules.Transactions;

/// <summary>
/// Die Position in der Timeline (#161) - und ob das, was ankam, ueberhaupt eine ist.
///
/// Der Cursor IST der <c>TimelineSortKey</c> der zuletzt gezeigten Zeile; nichts wird daraus
/// abgeleitet oder nachgerechnet. Das macht ihn als Vergleichswert direkt brauchbar und damit zum
/// Index-Bereich - und genau deshalb braucht er eine Pruefung: ein beliebiger Text ist ebenfalls ein
/// gueltiger Vergleichswert. <c>&lt; 'nonsense'</c> trifft jede Zeile, deren Schluessel mit einer
/// Ziffer beginnt, also alle. Die Anfrage saehe erfolgreich aus und begaenne irgendwo.
///
/// Der Schluessel hat eine feste Form: 1 Zeichen "vorgemerkt?", 8 Stellen Tagesnummer, 20 Stellen
/// Mikrosekunden, 32 Stellen Hex-Kennung.
/// </summary>
public static class TransactionCursor
{
    private const int PendingLength = 1;
    private const int DateLength = 8;
    private const int TimestampLength = 20;
    private const int IdLength = 32;
    private const int TotalLength = PendingLength + DateLength + TimestampLength + IdLength;

    /// <summary>
    /// Der Cursor, oder null. Null heisst "von vorn" - sichtbar von vorn, statt still einen Teil der
    /// Liste zu ueberspringen.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length != TotalLength) return null;

        if (trimmed[0] is not ('0' or '1')) return null;
        for (var index = PendingLength; index < PendingLength + DateLength + TimestampLength; index++)
            if (!char.IsAsciiDigit(trimmed[index])) return null;
        for (var index = TotalLength - IdLength; index < TotalLength; index++)
            if (!char.IsAsciiHexDigitLower(trimmed[index]) && !char.IsAsciiDigit(trimmed[index])) return null;

        return trimmed;
    }
}
