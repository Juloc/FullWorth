using System.Globalization;
using System.Text;

namespace FullWorth.Backend.Modules.Intelligence.Brands;

/// <summary>
/// Vom Namen auf der Buchung zum Kurznamen einer Marke - rein rechnerisch, ohne Netz und ohne KI.
///
/// Das ist die Stufe, die den Grossteil der Arbeit macht und nichts kostet. "VODAFONE WEST GMBH"
/// steht so in keinem Katalog; "vodafone" schon. Der Weg dahin ist immer derselbe: Rechtsformen und
/// Regionszusaetze hinten abschneiden, dann in absteigender Laenge probieren.
///
/// Die Klasse ist bewusst statisch und ohne Abhaengigkeiten. Sie liegt unter Brands/, damit
/// AutopilotArchitectureGuardTests sie als deterministische Schicht fuehren kann - dort darf der
/// Name eines KI-Anbieters nicht einmal vorkommen.
///
/// Portiert aus der abgeschafften Cloud (SimpleIconsBrandResearchProvider.CandidateSlugs,
/// NormalizeSlug, StripLegalSuffixes und PolishedBrandResearchProvider.ExpandedSlugs). Die Regeln
/// sind unveraendert: sie waren dort schon reine Funktionen ohne Fremddaten, und genau deshalb kann
/// eine einzelne Instanz sie selbst ausfuehren.
/// </summary>
public static class BrandSlugDerivation
{
    /// <summary>Mehr als zwoelf Versuche sind kein Erkennen mehr, sondern Raten.</summary>
    public const int MaximumCandidates = 12;

    /// <summary>
    /// Was hinten wegdarf. Rechtsformen stehen hier, aber auch Regions- und Sparten-Zusaetze:
    /// eine Buchung nennt die Gesellschaft, der Katalog die Marke.
    /// </summary>
    private static readonly HashSet<string> TrailingNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmbh", "mbh", "ag", "se", "kg", "kgaa", "ohg", "gbr", "ug", "ev", "eg",
        "ltd", "limited", "inc", "llc", "plc", "bv", "nv", "sa", "sarl", "srl", "spa", "as", "ab", "oy",
        "corp", "corporation", "company", "co",
        "group", "holding", "holdings", "services", "service", "vertrieb", "verwaltung",
        "payments", "payment", "deutschland", "germany", "austria", "oesterreich", "schweiz",
        "west", "ost", "nord", "sued", "north", "south", "east",
        "online", "store", "shop", "filiale", "markt", "market"
    };

    private static readonly char[] TokenSeparators = [' ', ',', '.', '-', '_', '/', '\\', '+'];

    /// <summary>
    /// Die Kurznamen zu einem Haendlernamen, bester zuerst.
    ///
    /// Erst der ganze Name, dann ohne Rechtsform, dann - Wort fuer Wort von hinten - immer kuerzere
    /// Anfaenge, zuletzt die Domain. Wer diese Liste abarbeitet, faengt beim Genauesten an; das ist
    /// der Grund fuer die Reihenfolge und nicht Zufall.
    /// </summary>
    public static IReadOnlyList<string> CandidateSlugs(string? merchantName, string? domain = null)
    {
        var candidates = new List<string>();

        void Add(string? value)
        {
            var slug = NormalizeSlug(value);
            if (slug.Length is > 0 and <= 80 && !candidates.Contains(slug, StringComparer.Ordinal))
                candidates.Add(slug);
        }

        var name = merchantName ?? string.Empty;
        Add(name);

        var tokens = name
            .Replace("&", " ", StringComparison.Ordinal)
            .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        while (tokens.Count > 1 && TrailingNoise.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
        Add(string.Join(' ', tokens));

        // Gegenparteien haengen Region oder Geschaeftsbereich an ("Vodafone West GmbH", "EDEKA Markt
        // 4711 Berlin"). Immer kuerzere Anfaenge zu probieren ist hier gefahrlos, weil jeder Treffer
        // gegen einen festen Katalog geprueft wird und Mehrdeutigkeit verworfen wird.
        while (tokens.Count > 1)
        {
            tokens.RemoveAt(tokens.Count - 1);
            Add(string.Join(' ', tokens));
        }

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var raw = domain.Trim();
            if (Uri.TryCreate(
                    raw.Contains("://", StringComparison.Ordinal) ? raw : "https://" + raw,
                    UriKind.Absolute,
                    out var uri))
            {
                var labels = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (labels.Length >= 2) Add(labels[^2]);
                else if (labels.Length == 1) Add(labels[0]);
            }
        }

        return candidates.Count <= MaximumCandidates
            ? candidates
            : candidates.Take(MaximumCandidates).ToArray();
    }

    /// <summary>
    /// Die Katalogform: klein, ohne Diakritika, nur Buchstaben und Ziffern, "&amp;" wird zu "and".
    ///
    /// Das "and" ist kein Schoenheitsfehler: der Simple-Icons-Katalog schreibt H&amp;M als "handm",
    /// und ohne diese Ersetzung fiele das Kaufmanns-Und ersatzlos weg.
    /// </summary>
    public static string NormalizeSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var expanded = value.Trim()
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("ẞ", "ss", StringComparison.Ordinal)
            .Replace("&", " and ", StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(expanded.Length);
        foreach (var character in expanded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Der kuerzeste Stamm, der den Haendlernamen noch eindeutig einer Marke zuordnet.
    ///
    /// Aus "EDEKA MARKT 4711 BERLIN" wird "EDEKA" - eine Zeile, die die ganze Kette abdeckt, statt
    /// einer Zeile je Filiale. Zu kurz ist gefaehrlich (ein Dreibuchstaben-Stamm trifft irgendwann
    /// alles), und mehrdeutig ist es auch: trifft der Stamm an einer Wortgrenze eine ZWEITE bekannte
    /// Marke, gibt es keinen Stamm. Dann bleibt nur der volle Name.
    /// </summary>
    public static string? UnambiguousStem(
        string aliasKey,
        string brandKey,
        Func<string, string?> brandForAlias,
        int minimumLength = 4)
    {
        if (string.IsNullOrWhiteSpace(aliasKey)) return null;

        var words = aliasKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = 1; take <= words.Length; take++)
        {
            var stem = string.Join(' ', words.Take(take));
            if (stem.Length < minimumLength) continue;

            var owner = brandForAlias(stem);
            // Noch unbekannt oder schon diese Marke: brauchbar. Eine ANDERE Marke: der Stamm gehoert
            // ihr, und ein zweiter Anspruch darauf waere genau die stille Fehlzuordnung, die eine
            // Katalogpflege nie wieder findet.
            if (owner is null || string.Equals(owner, brandKey, StringComparison.Ordinal))
                return stem;
            return null;
        }

        return null;
    }
}
