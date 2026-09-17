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
    // Das Dezimalkomma ist in SWIFT PFLICHT, die Stellen dahinter sind es nicht: eine runde Menge
    // steht als "10," da. Deshalb \d* und nicht \d+ - mit \d+ passte der Ausdruck auf "10," nirgends,
    // und aus der gemeldeten Menge wurde nichts.
    // "257,128493+EUR" - Betrag, Vorzeichen, Waehrung, in dieser Reihenfolge.
    private static readonly Regex CostAmount = new(@"^(?<value>\d+(?:[.,]\d*)?)(?<sign>[+-])(?<currency>[A-Z]{3})$", RegexOptions.Compiled);
    private static readonly Regex Amount = new(@"(?<currency>[A-Z]{3})?(?<value>-?\d+(?:[.,]\d*)?)\s*$", RegexOptions.Compiled);

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

    /// <summary>
    /// Der Fliesstext der Bloecke (:70E:) - mit MASKIERTEN Ziffern.
    ///
    /// Ein Bestand sagt, was er heute wert ist. Er sagt nicht, was er gekostet hat - und ohne den
    /// Einstand gibt es keinen Gewinn und keine Prozentzahl. Genau die zeigen andere Apps.
    ///
    /// :70E: ist das Feld, in dem die deutsche Auspraegung von MT535 zusaetzliche Angaben
    /// unterbringt, und die ING schickt es in JEDEM Block mit. Gelesen hat es hier noch nie jemand.
    /// Steht der Einstandskurs dort, ist er die ganze Zeit schon da gewesen.
    ///
    /// Was fehlt, ist nur die Kenntnis des Aufbaus - und die steckt in den BESCHRIFTUNGEN, nicht in
    /// den Zahlen. Deshalb bleiben die Woerter stehen und jede Ziffer wird zu '#': "Einstandskurs
    /// EUR ###,##" beantwortet die Frage vollstaendig und verraet keinen Betrag. Ein Depotbestand
    /// gehoert seinem Eigentuemer und hat in keinem Log etwas verloren.
    /// </summary>
    public static IReadOnlyList<string> NarrativeShape(string statement) =>
        Blocks(statement)
            .Select(block => block.TryGetValue("70E", out var values) ? string.Join(" ¦ ", values) : string.Empty)
            .Select(Masked)
            .ToArray();

    /// <summary>Jede Ziffer wird '#', jeder Zeilenumbruch '·'. Der Aufbau bleibt, der Inhalt geht.</summary>
    private static string Masked(string text) =>
        text.Length == 0 ? "-" : string.Concat(text.Select(c => char.IsDigit(c) ? '#' : c == '\n' ? '·' : c));
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
        var (costPrice, costCurrency) = ReadCostPrice(First(fields, "70E"));

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
            exchange,
            costPrice,
            costCurrency);
    }

    /// <summary>
    /// Der Einstandskurs JE STUECK aus dem Fliesstext - der einzige Weg, ohne eingetippte Kaeufe
    /// einen Gewinn zu rechnen.
    ///
    /// Die ING schickt ihn in jedem Block:
    ///
    /// <code>
    /// :70E::HOLD//1STK
    /// 257,128493+EUR
    /// </code>
    ///
    /// <c>1STK</c> nennt die Bezugsgroesse - je 1 Stueck -, danach folgt der Betrag mit Vorzeichen
    /// und Waehrung. Dass es der Kurs je Stueck ist und nicht der Einstandswert, sagen die Daten
    /// selbst: ein Wert waere ein Geldbetrag mit zwei Nachkommastellen. Drei der vier Positionen
    /// tragen SECHS, und ausgerechnet die mit glatt 10 Stueck traegt zwei - das ist eine Division
    /// durch die Stueckzahl, keine Summe.
    ///
    /// Der Einstandswert ist deshalb <c>Stueckzahl * Einstandskurs</c> und wird hier NICHT gebildet:
    /// die Stueckzahl steht in einem anderen Feld, und zwei Felder zu verrechnen ist Auslegung, nicht
    /// Lesen. Nennt die Bank keinen Kurs, bleibt es leer - kein Ersatzwert.
    /// </summary>
    private static (decimal? Price, string? Currency) ReadCostPrice(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return (null, null);

        // Erste Zeile ist die Bezugsgroesse, der Betrag steht darunter.
        var lines = field.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return (null, null);

        var match = CostAmount.Match(lines[^1].Trim());
        if (!match.Success) return (null, null);

        var text = match.Groups["value"].Value.Replace(',', '.').TrimEnd('.');
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return (null, null);

        var sign = match.Groups["sign"].Value == "-" ? -1m : 1m;
        return (sign * value, match.Groups["currency"].Value);
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
        // "10," wird zu "10." und danach zu "10" - ein Trennzeichen ohne Stellen dahinter trennt nichts.
        var text = match.Groups["value"].Value.Replace(',', '.').TrimEnd('.');
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
