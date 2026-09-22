namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Wofuer eine KI arbeiten darf.
///
/// Das gab es vorher schon - als feste Spalten: <c>ReceiptAiEnabled</c>, <c>MerchantAiEnabled</c>,
/// <c>CategoryAiEnabled</c>, <c>ContractAiEnabled</c>, <c>ProductAiEnabled</c> auf der Instanz, und
/// dieselben fuenf noch einmal beim Benutzer. Eine neue Funktion kostete damit zwei Spalten, eine
/// Migration, zwei DTO-Felder und ein Formularfeld, und die fuenf beim Benutzer las am Ende niemand.
///
/// Hier ist ein Modul eine Zeichenkette in einer Tabelle. Eine neue Funktion ist eine Zeile in dieser
/// Datei plus ein Text in den zwei Sprachdateien - kein Schemawechsel.
///
/// <see cref="LogoResearch"/> und <see cref="InternetResearch"/> stehen hier, obwohl die Recherche
/// selbst noch nicht gebaut ist. Das ist Absicht: der Besitzer hat sie ausdruecklich eingeplant, und
/// ein Modul im Katalog kostet nichts ausser der Zeile. Freigeben laesst sich beides schon; es
/// passiert nur noch nichts damit.
/// </summary>
public static class AiModules
{
    /// <summary>Haendler zu Kategorie. Bisher die zwei Schalter MerchantAiEnabled UND CategoryAiEnabled,
    /// die ohnehin nur gemeinsam abgefragt wurden - es ist eine Funktion, nicht zwei.</summary>
    public const string Categorization = "categorization";

    /// <summary>Belege und Rechnungen lesen.</summary>
    public const string Receipts = "receipts";

    /// <summary>Artikel und Produkte aus Kaeufen erkennen.</summary>
    public const string Products = "products";

    /// <summary>Wiederkehrende Zahlungen als Vertrag erkennen.</summary>
    public const string Contracts = "contracts";

    /// <summary>Der Finanz-Coach.</summary>
    public const string Coach = "coach";

    /// <summary>
    /// Kandidaten einer Sammlung nachbewerten und begruenden (#124). Die Kandidaten selbst findet
    /// das deterministische System; diese Stufe ordnet sie und sagt, warum.
    /// </summary>
    public const string CollectionSuggestions = "collection-suggestions";

    /// <summary>Logos zu Haendlern finden, wenn keine Cloud angebunden ist.</summary>
    public const string LogoResearch = "logo-research";

    /// <summary>Offene Fragen im Netz nachschlagen, wenn keine Cloud angebunden ist.</summary>
    public const string InternetResearch = "internet-research";

    public static readonly IReadOnlyList<string> All =
    [
        Categorization,
        Receipts,
        Products,
        Contracts,
        Coach,
        CollectionSuggestions,
        LogoResearch,
        InternetResearch
    ];

    /// <summary>
    /// Eine Freigabe fuer ein Modul, das es nicht gibt, ist ein Tippfehler und keine Freigabe - sie
    /// wuerde still nie greifen. Deshalb wird beim Schreiben geprueft und nicht beim Lesen.
    /// </summary>
    public static bool IsKnown(string? module) =>
        module is not null && All.Contains(module, StringComparer.Ordinal);

    public static string? Normalize(string? module)
    {
        var value = module?.Trim().ToLowerInvariant();
        return IsKnown(value) ? value : null;
    }
}
