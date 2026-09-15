namespace FullWorth.Backend.Data;

/// <summary>
/// Die Namen der Standardkategorien je Sprache. Die SCHLUESSEL sind in beiden Sprachen dieselben und
/// aendern sich nie - <c>GermanyCategorizationCatalog</c> und jede Regel zielen auf den Schluessel,
/// nicht auf den Namen. Deshalb kann ein Benutzer jede Kategorie umbenennen, ohne die automatische
/// Zuordnung zu zerstoeren, und deshalb ist eine zweite Sprache hier nur eine zweite Namensliste.
///
/// Bis 2026-09-15 gab es nur Englisch, und der Einrichtungsassistent fragte nicht. Wer FullWorth auf
/// Deutsch benutzte, bekam englische Standardkategorien - und sobald ein Import deutsche Kategorien
/// anlegte, standen beide nebeneinander. Genau das meldet #117 als "gemischtes Set".
/// </summary>
public static class DefaultCategoryNames
{
    public const string German = "de";
    public const string English = "en";

    public static string Normalize(string? language) =>
        string.Equals(language?.Trim(), German, StringComparison.OrdinalIgnoreCase) ? German : English;

    /// <summary>Der Name eines Schluessels in der gewaehlten Sprache; ohne Uebersetzung bleibt es beim englischen.</summary>
    public static string For(string key, string language, string englishName) =>
        Normalize(language) == German && GermanNames.TryGetValue(key, out var german) ? german : englishName;

    private static readonly Dictionary<string, string> GermanNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["income"] = "Einnahmen",
        ["income.salary"] = "Gehalt",
        ["income.benefits"] = "Sozialleistungen",
        ["income.refunds"] = "Erstattungen & Cashback",
        ["income.interest"] = "Zinsen",
        ["income.other"] = "Sonstige Einnahmen",

        ["housing"] = "Wohnen",
        ["housing.rent"] = "Miete",
        ["housing.mortgage"] = "Hypothek",
        ["housing.electricity"] = "Strom",
        ["housing.heating"] = "Heizung & Gas",
        ["housing.water"] = "Wasser & Abwasser",
        ["housing.internet"] = "Internet & Telefon",
        ["housing.utilities"] = "Weitere Nebenkosten",

        ["food"] = "Essen",
        ["food.groceries"] = "Lebensmittel",
        ["food.bakery"] = "Bäckerei",
        ["food.restaurants"] = "Restaurants",
        ["food.delivery"] = "Lieferdienste",

        ["transport"] = "Verkehr",
        ["transport.public"] = "Öffentliche Verkehrsmittel",
        ["transport.taxi"] = "Taxi & Fahrdienste",

        ["vehicle"] = "Fahrzeug",
        ["vehicle.fuel"] = "Kraftstoff",
        ["vehicle.charging"] = "Laden",
        ["vehicle.maintenance"] = "Wartung",
        ["vehicle.carwash"] = "Autowäsche",
        ["vehicle.parking"] = "Parken & Maut",

        ["shopping"] = "Einkaufen",
        ["shopping.household"] = "Haushalt",
        ["shopping.drugstore"] = "Drogerie",
        ["shopping.electronics"] = "Elektronik",
        ["shopping.clothing"] = "Kleidung",
        ["shopping.furniture"] = "Möbel",
        ["shopping.hardware"] = "Baumarkt",
        ["shopping.books"] = "Bücher",
        ["shopping.beauty"] = "Beauty",

        ["health"] = "Gesundheit",
        ["health.pharmacy"] = "Apotheke",
        ["health.doctor"] = "Arzt",
        ["health.dental"] = "Zahnarzt",
        ["health.optical"] = "Optiker",

        ["insurance"] = "Versicherungen",
        ["insurance.health"] = "Krankenversicherung",

        ["subscriptions"] = "Abonnements",
        ["subscriptions.streaming"] = "Streaming",
        ["subscriptions.software"] = "Software & Cloud",

        ["leisure"] = "Freizeit",
        ["leisure.sports"] = "Sport & Fitness",
        ["leisure.gaming"] = "Gaming",
        ["leisure.events"] = "Kino & Veranstaltungen",

        ["travel"] = "Reisen",
        ["travel.flights"] = "Flüge",
        ["travel.accommodation"] = "Hotels & Unterkünfte",
        ["travel.packages"] = "Pauschalreisen & Kreuzfahrten",

        ["education"] = "Bildung",

        ["family"] = "Familie",
        ["family.childcare"] = "Kinderbetreuung",

        ["pets"] = "Haustiere",
        ["pets.food"] = "Tierfutter",
        ["pets.vet"] = "Tierarzt",

        ["cash"] = "Bargeld",
        ["fees"] = "Gebühren",
        ["taxes"] = "Steuern",
        ["donations"] = "Spenden",
        ["savings"] = "Sparen & Anlegen",
        ["debt"] = "Kredite & Darlehen",
        ["transfers"] = "Umbuchungen",
        ["other"] = "Sonstiges"
    };

    /// <summary>Jeder Schluessel, fuer den es eine deutsche Fassung gibt - fuer den Test, der behauptet: alle.</summary>
    public static IReadOnlyCollection<string> TranslatedKeys => GermanNames.Keys;
}
