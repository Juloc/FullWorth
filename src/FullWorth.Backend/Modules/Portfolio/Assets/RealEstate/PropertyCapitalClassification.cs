namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>Wie eine Ausgabe an einer Immobilie zaehlt.</summary>
public static class PropertyImprovementTreatments
{
    /// <summary>Erhaltungsaufwand: laufende Kosten. Erhoeht das investierte Kapital nicht.</summary>
    public const string Maintenance = "maintenance";

    /// <summary>Herstellungskosten: erhoeht das investierte Kapital.</summary>
    public const string ValueIncreasing = "value_increasing";

    public static bool IsKnown(string? value) => value is Maintenance or ValueIncreasing;
}

/// <summary>Ein Hinweis, dass eine Faustregel greift - kein Urteil, sondern eine Nachfrage.</summary>
public sealed record PropertyCapitalHint(string Code, IReadOnlyList<Guid> ImprovementIds);

/// <summary>Eine Massnahme, so wie die Einstufung sie braucht.</summary>
public sealed record PropertyImprovementFacts(
    Guid Id,
    string Category,
    decimal? Cost,
    DateOnly? CompletedDate,
    string? Treatment);

/// <summary>
/// Instandhaltung oder wertsteigernd - und wann FullWorth nachfragt (#174).
///
/// Die Frage entscheidet, ob eine Ausgabe laufende Kosten sind oder das investierte Kapital erhoeht,
/// und daran haengen Gewinn, Wertsteigerung und Eigenkapital. Sie ist in der Praxis ein Grenzfall,
/// und es gibt dafuer eine etablierte Systematik:
///
/// <list type="bullet">
/// <item><b>Gleichwertiger Ersatz ist Erhaltung.</b> Alte Gastherme raus, neue Gastherme rein.</item>
/// <item><b>Standardhebung ist Herstellung.</b> Gastherme raus, Waermepumpe rein.</item>
/// </list>
///
/// Das kann eine Anwendung nicht aus einer Kategorie ablesen: "heating" ist beides. Deshalb ist die
/// Voreinstellung eine VERMUTUNG und kein Urteil - der Benutzer stellt je Posten um, und was er
/// eingestellt hat, gilt.
///
/// Zwei Faustregeln entscheiden es in der Praxis trotzdem oft, und beide haengen an Dingen, die
/// FullWorth kennt. Sie stellen die Einstufung NICHT um; sie fragen nach. Eine Anwendung, die
/// steuerliche Folgen still umbucht, ist an der falschen Stelle klug.
/// </summary>
public static class PropertyCapitalClassification
{
    /// <summary>
    /// Die vier Kernbereiche der Drei-von-vier-Regel. Werden innerhalb von fuenf Jahren mindestens
    /// drei davon gehoben, gilt ueblicherweise alles als Herstellungskosten.
    /// </summary>
    public static readonly IReadOnlyList<string> CoreAreas = ["heating", "plumbing", "electrical", "windows"];

    public const string ThreeOfFourHint = "three_of_four_core_areas";
    public const string FifteenPercentHint = "fifteen_percent_within_three_years";

    private const int CoreAreaWindowYears = 5;
    private const int PurchaseWindowYears = 3;
    private const decimal PurchaseShareThreshold = 0.15m;

    /// <summary>
    /// Kategorien, bei denen ein Umbau ueberwiegend den Standard hebt, statt Bestehendes zu ersetzen.
    /// Das ist die Voreinstellung und nichts weiter - "bathroom" kann eine neue Dichtung sein.
    /// </summary>
    private static readonly HashSet<string> ValueIncreasingByDefault =
        new(StringComparer.OrdinalIgnoreCase) { "solar", "insulation", "structural" };

    /// <summary>
    /// Was gilt: die Angabe des Benutzers, sonst die Vermutung aus der Kategorie. Er ueberstimmt sie
    /// immer - eine Voreinstellung, die sich nicht ueberstimmen laesst, ist keine.
    /// </summary>
    public static string Effective(string? treatment, string? category) =>
        PropertyImprovementTreatments.IsKnown(treatment)
            ? treatment!
            : DefaultFor(category);

    public static string DefaultFor(string? category) =>
        category is not null && ValueIncreasingByDefault.Contains(category.Trim())
            ? PropertyImprovementTreatments.ValueIncreasing
            : PropertyImprovementTreatments.Maintenance;

    /// <summary>
    /// Das investierte Kapital: Kaufpreis samt Nebenkosten plus alles, was wertsteigernd eingestuft
    /// ist. Erhaltungsaufwand zaehlt NICHT mit - er ist verbraucht, nicht investiert.
    /// </summary>
    public static decimal InvestedCapital(
        decimal purchasePrice,
        decimal acquisitionCosts,
        IReadOnlyList<PropertyImprovementFacts> improvements)
    {
        var added = improvements
            .Where(item => Effective(item.Treatment, item.Category) == PropertyImprovementTreatments.ValueIncreasing)
            .Sum(item => item.Cost ?? 0m);
        return purchasePrice + acquisitionCosts + added;
    }

    /// <summary>
    /// Wo FullWorth nachfragt. Beide Regeln betrachten nur Massnahmen, die der Benutzer NICHT bereits
    /// als wertsteigernd eingestuft hat - zu einer Einstufung zu raten, die schon so ist, waere eine
    /// Meldung ohne Inhalt.
    /// </summary>
    public static IReadOnlyList<PropertyCapitalHint> Hints(
        IReadOnlyList<PropertyImprovementFacts> improvements,
        DateOnly? purchaseDate,
        decimal? buildingPurchasePrice)
    {
        var open = improvements
            .Where(item => item.CompletedDate.HasValue)
            .Where(item => Effective(item.Treatment, item.Category) != PropertyImprovementTreatments.ValueIncreasing)
            .ToList();
        if (open.Count == 0) return [];

        var hints = new List<PropertyCapitalHint>();

        // Drei von vier Kernbereichen innerhalb von fuenf Jahren. Gezaehlt werden BEREICHE, nicht
        // Massnahmen: zweimal die Heizung in fuenf Jahren ist ein Bereich, nicht zwei.
        foreach (var candidate in open)
        {
            var from = candidate.CompletedDate!.Value;
            var until = from.AddYears(CoreAreaWindowYears);
            var window = improvements
                .Where(item => item.CompletedDate is { } done && done >= from && done <= until)
                .ToList();
            var areas = window
                .Select(item => item.Category?.Trim().ToLowerInvariant())
                .Where(category => category is not null && CoreAreas.Contains(category))
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (areas < 3) continue;

            hints.Add(new PropertyCapitalHint(
                ThreeOfFourHint,
                window.Where(item => open.Any(candidate2 => candidate2.Id == item.Id))
                    .Select(item => item.Id).Distinct().ToArray()));
            break;
        }

        // Mehr als 15 % des Gebaeude-Kaufpreises innerhalb von drei Jahren nach dem Kauf.
        if (purchaseDate is { } bought && buildingPurchasePrice is > 0m)
        {
            var until = bought.AddYears(PurchaseWindowYears);
            var withinWindow = improvements
                .Where(item => item.CompletedDate is { } done && done >= bought && done <= until)
                .ToList();
            var spent = withinWindow.Sum(item => item.Cost ?? 0m);
            if (spent > buildingPurchasePrice.Value * PurchaseShareThreshold)
            {
                var affected = withinWindow
                    .Where(item => open.Any(candidate => candidate.Id == item.Id))
                    .Select(item => item.Id).Distinct().ToArray();
                if (affected.Length > 0)
                    hints.Add(new PropertyCapitalHint(FifteenPercentHint, affected));
            }
        }

        return hints;
    }
}
