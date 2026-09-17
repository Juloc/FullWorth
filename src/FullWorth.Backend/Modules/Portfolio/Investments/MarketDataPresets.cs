namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>
/// Eine fertige Kursquelle: wohin gefragt wird und wo im Ergebnis die Zahlen stehen.
///
/// <c>PriceUrl</c> und <c>SearchUrl</c> sind Vorlagen. Ersetzt werden <c>{symbol}</c>, <c>{isin}</c>,
/// <c>{wkn}</c>, <c>{from}</c> und <c>{to}</c> (Unix-Sekunden) sowie <c>{apiKey}</c>.
///
/// Die Pfade sind Punktpfade in die Antwort; <c>[0]</c> waehlt ein Element. Zwei Reihen, die
/// nebeneinander liegen - Zeitpunkte und Werte -, weil praktisch jede Kurs-Schnittstelle so baut.
/// </summary>
public sealed record MarketDataPreset(
    string Key,
    string Name,
    string Note,
    string PriceUrl,
    string DatePath,
    string ValuePath,
    string? CurrencyPath = null,
    string? SearchUrl = null,
    string? SearchSymbolPath = null,
    bool NeedsApiKey = false);

/// <summary>
/// Die Kursquellen, die man auswaehlen kann, statt eine Vorlage von Hand zu bauen.
///
/// Warum ueberhaupt auswaehlbar und nicht fest eingebaut: FullWorth darf keine Abhaengigkeit von
/// einem fremden Dienst mitbringen. Wer nichts einstellt, bekommt keine Kurse - und merkt das an
/// einer klaren Auskunft, nicht an erfundenen Zahlen. Wer etwas einstellt, entscheidet selbst, wen
/// er fragt und unter wessen Nutzungsbedingungen.
///
/// Warum trotzdem Voreinstellungen: "URL-Vorlage und JSON-Pfad" ist keine Frage, die man einem
/// Eigentuemer beim Einrichten stellen sollte. Ein Name genuegt; die Vorlage steht hier.
///
/// Die Auswahl ist duenn, und das ist kein Versehen. Portfolio Performance empfiehlt von all seinen
/// Quellen nur noch zwei, Ghostfolio fuehrt ausser der manuellen nur Yahoo als offiziell. Beide
/// Angaben sind am 2026-09-17 geprueft worden, und die Yahoo-Vorlagen unten ebenfalls - mit einer
/// echten Abfrage, nicht aus dem Gedaechtnis:
///
/// <code>
/// v1/finance/search?q=IE00B4L5Y983  ->  IWDA.L
/// v8/finance/chart/IWDA.AS          ->  timestamp[], close[], meta.currency = EUR
/// </code>
///
/// Yahoo ist inoffiziell und an Nutzungsbedingungen fuer den Privatgebrauch gebunden. Deshalb steht
/// es hier als WAHL und nicht als Voreinstellung: <c>None</c> ist der Ausgangszustand.
/// </summary>
public static class MarketDataPresets
{
    public const string None = "none";
    public const string Custom = "custom";

    /// <summary>
    /// Yahoo Finance. Kein Schluessel noetig, findet ein Papier ueber seine ISIN und liefert
    /// Tagesschlusskurse.
    ///
    /// <c>adjclose</c> statt <c>close</c>: bei ausschuettenden Papieren ist der bereinigte Kurs der,
    /// aus dem eine Rendite ohne Sprung an jedem Ausschuettungstag entsteht. Bei thesaurierenden
    /// ETFs sind beide gleich - es kostet also nichts und rettet den anderen Fall.
    /// </summary>
    public static readonly MarketDataPreset Yahoo = new(
        "yahoo",
        "Yahoo Finance",
        "Ohne Schlüssel. Findet Papiere über die ISIN. Inoffiziell und für den privaten Gebrauch.",
        "https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?period1={from}&period2={to}&interval=1d",
        "chart.result[0].timestamp",
        "chart.result[0].indicators.adjclose[0].adjclose",
        "chart.result[0].meta.currency",
        "https://query1.finance.yahoo.com/v1/finance/search?q={isin}&quotesCount=1",
        "quotes[0].symbol");

    /// <summary>
    /// Stooq. Liefert CSV statt JSON und wird deshalb hier NICHT als Vorlage angeboten - eine
    /// Voreinstellung, die nur fast passt, kostet mehr Zeit als keine. Wer sie will, nimmt
    /// <c>custom</c>.
    /// </summary>
    public static IReadOnlyList<MarketDataPreset> All { get; } = [Yahoo];

    public static MarketDataPreset? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(preset => preset.Key == key.Trim());

    /// <summary>Was in den Einstellungen zur Auswahl steht - samt der beiden Sonderfaelle.</summary>
    public static IReadOnlyList<string> Choices { get; } = [None, .. All.Select(preset => preset.Key), Custom];
}
