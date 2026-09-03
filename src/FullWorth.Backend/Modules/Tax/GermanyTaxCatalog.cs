namespace FullWorth.Backend.Modules.Tax;

public static class GermanyTaxCatalog
{
    public const int FirstSupportedYear = 2026;

    public sealed record Definition(string Code, string? ParentCode, string Name, string Description);

    public static readonly IReadOnlyList<Definition> Definitions =
    [
        new("werbungskosten", null, "Werbungskosten", "Mögliche beruflich veranlasste Aufwendungen."),
        new("werbungskosten.arbeitsmittel", "werbungskosten", "Arbeitsmittel", "Mögliche beruflich genutzte Arbeitsmittel."),
        new("werbungskosten.software", "werbungskosten", "Berufliche Software", "Mögliche beruflich genutzte Software und digitale Dienste."),
        new("werbungskosten.fortbildung", "werbungskosten", "Fortbildung", "Mögliche beruflich veranlasste Fort- oder Weiterbildung."),
        new("werbungskosten.fachliteratur", "werbungskosten", "Fachliteratur", "Mögliche beruflich veranlasste Fachliteratur."),
        new("werbungskosten.fahrtkosten", "werbungskosten", "Fahrtkosten", "Mögliche beruflich veranlasste Fahrtkosten."),
        new("werbungskosten.bewerbung", "werbungskosten", "Bewerbungskosten", "Mögliche Kosten im Zusammenhang mit Bewerbungen."),
        new("sonderausgaben", null, "Sonderausgaben", "Mögliche Sonderausgaben; Einzelfallprüfung erforderlich."),
        new("sonderausgaben.versicherungen", "sonderausgaben", "Versicherungen", "Möglicherweise steuerlich relevante Versicherungsbeiträge."),
        new("haushalt.nahe_dienstleistungen", null, "Haushaltsnahe Dienstleistungen", "Möglicherweise begünstigte haushaltsnahe Dienstleistungen."),
        new("haushalt.handwerker", null, "Handwerkerleistungen", "Möglicherweise begünstigte Handwerkerleistungen; Arbeits- und Materialkosten können unterschiedlich zu behandeln sein."),
        new("spenden", null, "Spenden", "Möglicherweise steuerlich relevante Spenden oder Zuwendungen."),
        new("aussergewoehnliche_belastungen", null, "Außergewöhnliche Belastungen", "Mögliche außergewöhnliche Belastungen; Einzelfallprüfung erforderlich."),
        new("sonstige", null, "Sonstige mögliche Steuerfälle", "Nicht eindeutig zuordenbarer möglicher Steuerfall.")
    ];

    public static string RuleVersion(int taxYear) => $"DE-{taxYear}-v1";
}
