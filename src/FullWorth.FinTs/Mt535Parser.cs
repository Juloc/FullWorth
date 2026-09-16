using System.Globalization;
using System.Text.RegularExpressions;

namespace FullWorth.FinTs;

/// <summary>
/// Liest die Depotaufstellung, die HIWPD als MT535 mitbringt (#130 §5).
///
/// Vorher gab es das nicht. Der Bestand wurde aus den Feldern des Segments GERATEN: die erste Zahl
/// galt als Stueckzahl, die zweite als Kurs, die dritte als Wert, und was uebrig blieb, hiess
/// "Wertpapier". Das ist keine Auslegung eines Formats, sondern eine Vermutung - und sie ist falsch,
/// sobald ein Feld fehlt oder eines dazukommt. Ein Depot mit falscher Stueckzahl sieht aus wie ein
/// Depot; auffallen wuerde es erst am Vermoegen.
///
/// MT535 ist ein SWIFT-Format aus Bloecken. Was hier gebraucht wird, steht in den Feldern eines
/// <c>FIN</c>-Blocks innerhalb von <c>SUBSAFE</c>:
///
/// <code>
/// :35B:ISIN DE0007164600     Kennung und Name (mehrzeilig)
///      SAP SE
/// :93B::AGGR//UNIT/12,5      Stueckzahl (FAMT bei Nominalwerten)
/// :90B::MRKT//ACTU/EUR120,5  Kurs in Waehrung  (90A: in Prozent)
/// :98A::PRIC//20260915       Kursdatum
/// :19A::HOLD//EUR1506,25     Marktwert
/// :94B::SAFE//EXCH/XETR      Handelsplatz
/// </code>
///
/// Gelesen wird nur, was dasteht. Fehlt ein Feld, bleibt der Wert leer - keine Ersatzzahl, kein
/// Ersatzname. Ein Bestand ohne Kennung UND ohne Namen wird verworfen, weil er nichts benennt.
/// </summary>
public static class Mt535Parser
{
    private static readonly Regex BlockStart = new(@"^:16R:(?<name>\w+)\s*$", RegexOptions.Compiled);
    private static readonly Regex BlockEnd = new(@"^:16S:(?<name>\w+)\s*$", RegexOptions.Compiled);
    private static readonly Regex FieldStart = new(@"^:(?<tag>\d{2}[A-Z]?):(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex Isin = new(@"\b(?<isin>[A-Z]{2}[A-Z0-9]{9}\d)\b", RegexOptions.Compiled);
    private static readonly Regex Amount = new(@"(?<currency>[A-Z]{3})?(?<value>-?\d+(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);

    /// <summary>Wahr, wenn der Text wie eine MT535-Aufstellung aussieht.</summary>
    public static bool LooksLikeStatement(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains(":16R:", StringComparison.Ordinal) && text.Contains(":35B:", StringComparison.Ordinal);

    /// <summary>
    /// Welche Felder in jedem FIN-Block standen - die KENNUNGEN, nicht ihr Inhalt.
    ///
    /// "3 von 4 ETF" ist eine andere Frage als "null Bestaende": ein Block ist da und faellt trotzdem
    /// heraus. Zwei Stellen koennen das tun, und beide taten es stumm - ein Bestand ohne :35B: wird
    /// verworfen, und einer ohne lesbare Menge wird bei der Uebernahme uebersprungen.
    ///
    /// Die Tags sagen, welcher Fall es ist: fehlt dem vierten Block das Mengenfeld, steht seine Menge
    /// in einem Feld, das hier nicht gelesen wird, oder fehlt die Kennung. Namen, Kennnummern und
    /// Betraege bleiben drin, wo sie hingehoeren.
    /// </summary>
    public static IReadOnlyList<string> FieldShape(string statement) =>
        Blocks(statement).Select(block => string.Join("+", block.Keys.Order(StringComparer.Ordinal))).ToArray();

    /// <summary>Die Bestaende der Aufstellung, in der Reihenfolge, in der sie dastehen.</summary>
    public static IReadOnlyList<FinTsHolding> Parse(string statement) =>
        Blocks(statement).Select(Build).OfType<FinTsHolding>().ToArray();

    /// <summary>
    /// Die FIN-Bloecke der Aufstellung, jeder als seine Felder. Nur die Zerlegung, keine Deutung.
    ///
    /// Der Bestand ist der FIN-Block. Nur er.
    ///
    /// Hier wurde vorher ein umschliessendes :16R:SUBSAFE VERLANGT, und ohne das wurde keine
    /// einzige Zeile gelesen. Die ING schickt keines - ihre FIN-Bloecke stehen direkt nach GENL.
    /// Das Ergebnis war ein Depot mit vier ETFs und null Bestaenden, waehrend die Bank 1388
    /// Zeichen MT535 geliefert hatte:
    ///
    ///   HIWPD=1, v6:MT535:1388-Zeichen, Bestaende=0
    ///
    /// SUBSAFE ist eine Klammer, kein Inhalt, und in MT535 nicht garantiert. Was zaehlt, ist FIN:
    /// alles zwischen :16R:FIN und dem zugehoerigen :16S:FIN gehoert zu EINEM Bestand, auch die
    /// Felder seiner Unterbloecke (FINSUB, SUBBAL). Alles ausserhalb - GENL, ADDINFO - ist keiner.
    /// </summary>
    private static IEnumerable<Dictionary<string, List<string>>> Blocks(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement)) yield break;

        // Ein Feld darf ueber mehrere Zeilen gehen (der Name in :35B: tut es fast immer).
        var depth = 0;
        Dictionary<string, List<string>>? current = null;
        string? openTag = null;

        foreach (var raw in statement.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            var start = BlockStart.Match(line);
            if (start.Success)
            {
                openTag = null;
                if (current is null)
                {
                    // Ausserhalb eines Bestands zaehlt nur der Anfang eines neuen.
                    if (start.Groups["name"].Value == "FIN") { current = []; depth = 1; }
                    continue;
                }
                depth++;
                continue;
            }

            var end = BlockEnd.Match(line);
            if (end.Success)
            {
                openTag = null;
                if (current is null) continue;
                if (--depth > 0) continue;
                yield return current;
                current = null;
                depth = 0;
                continue;
            }

            if (current is null) continue;

            var field = FieldStart.Match(line);
            if (field.Success)
            {
                openTag = field.Groups["tag"].Value;
                if (!current.TryGetValue(openTag, out var values)) current[openTag] = values = [];
                values.Add(field.Groups["rest"].Value.Trim());
                continue;
            }

            // Fortsetzungszeile des zuletzt begonnenen Feldes.
            if (openTag is not null && current.TryGetValue(openTag, out var open) && open.Count > 0)
                open[^1] = (open[^1] + "\n" + line.Trim()).Trim();
        }

        // Eine Aufstellung, der das letzte :16S:FIN fehlt, ist trotzdem eine Aufstellung.
        if (current is not null) yield return current;
    }

    private static FinTsHolding? Build(Dictionary<string, List<string>> fields)
    {
        var identification = First(fields, "35B");
        if (identification is null) return null;

        string? isin = null;
        var nameParts = new List<string>();
        foreach (var part in identification.Split('\n'))
        {
            var text = part.Trim();
            if (text.Length == 0) continue;
            var match = Isin.Match(text);
            if (isin is null && match.Success && text.StartsWith("ISIN", StringComparison.OrdinalIgnoreCase))
            {
                isin = match.Groups["isin"].Value;
                var rest = text[(text.IndexOf(isin, StringComparison.Ordinal) + isin.Length)..].Trim();
                if (rest.Length > 0) nameParts.Add(rest);
                continue;
            }
            nameParts.Add(text);
        }

        // Die WKN steht, wo sie ueberhaupt steht, im Zusatzblock - sechs Stellen, keine ISIN.
        var wkn = First(fields, "94B") is { } place && place.Length == 6 && place.All(char.IsLetterOrDigit) ? place : null;
        var name = string.Join(" ", nameParts).Trim();
        if (name.Length == 0 && isin is null) return null;

        var (quantity, _) = ReadAmount(First(fields, "93B") ?? First(fields, "93C") ?? First(fields, "93A"));
        var (price, priceCurrency) = ReadAmount(First(fields, "90B") ?? First(fields, "90A"));
        var (market, marketCurrency) = ReadAmount(First(fields, "19A"));
        var priceDate = ReadDate(First(fields, "98A") ?? First(fields, "98C"));
        var exchange = ReadExchange(First(fields, "94B"));

        return new FinTsHolding(
            isin,
            wkn,
            name.Length > 0 ? name : isin!,
            quantity ?? 0m,
            price,
            priceCurrency,
            priceDate,
            market,
            marketCurrency ?? priceCurrency,
            exchange);
    }

    private static string? First(Dictionary<string, List<string>> fields, string tag) =>
        fields.TryGetValue(tag, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>
    /// Der Zahlenwert am Ende eines Qualifier-Feldes, samt Waehrung, wenn eine davorsteht.
    /// <c>::AGGR//UNIT/12,5</c> ergibt 12,5 ohne Waehrung, <c>::HOLD//EUR1506,25</c> ergibt 1506,25 in EUR.
    /// </summary>
    private static (decimal? Value, string? Currency) ReadAmount(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return (null, null);
        var tail = field.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
        var match = Amount.Match(tail);
        if (!match.Success) return (null, null);
        // MT535 schreibt Dezimalstellen mit Komma; ein Punkt kommt nicht als Tausendertrenner vor.
        var text = match.Groups["value"].Value.Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            return (null, null);
        var currency = match.Groups["currency"].Success ? match.Groups["currency"].Value : null;
        return (value, currency);
    }

    private static DateOnly? ReadDate(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return null;
        var tail = field.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
        var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
        return digits.Length >= 8
            && DateOnly.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Der Handelsplatz aus <c>::SAFE//EXCH/XETR</c>. Ohne EXCH-Kennzeichnung ist es keiner.</summary>
    private static string? ReadExchange(string? field)
    {
        if (string.IsNullOrWhiteSpace(field) || !field.Contains("EXCH", StringComparison.Ordinal)) return null;
        var parts = field.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var value = parts[^1].Trim();
        return value.Length is > 0 and <= 10 ? value : null;
    }
}
