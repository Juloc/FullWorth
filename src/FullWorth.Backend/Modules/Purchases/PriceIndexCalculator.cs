using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>Ein gekaufter Artikel, so wie der Preisindex ihn braucht: wer ihn verkauft hat, was er war, was eine Einheit kostete.</summary>
public sealed record PriceObservation(string ItemKey, string? GroupKey, DateOnly Date, decimal UnitPrice, decimal Spend);

/// <summary>Ein Punkt des Preisindex: das Quartal (Beginn), der Wert (erstes Quartal = 100) und auf wie vielen Artikeln er beruht.</summary>
public sealed record PriceIndexPoint(string Quarter, DateOnly Start, decimal Index, int MatchedItems);

/// <summary>
/// Wie sich die Preise der Dinge entwickelt haben, die jemand wirklich kauft - gerechnet aus seinen eigenen
/// bestaetigten Belegen, ohne dass die Artikel einem Produkt zugeordnet sein muessen.
///
/// Verkettet von Quartal zu Quartal (so wie die amtliche Statistik ihre Indizes verkettet): verglichen werden
/// nur Artikel, die im selben Geschaeft und unter demselben Namen in beiden Quartalen gekauft wurden, mit dem
/// Median ihres Einheitspreises je Quartal und den Ausgaben des frueheren Quartals als Gewicht. Ein Sortiment,
/// das sich aendert, und ein anderes Geschaeft verfaelschen die Rate deshalb nicht: sie fallen aus dem Vergleich.
/// Hat ein Quartal zu wenige vergleichbare Artikel, bekommt es keinen Punkt - eine Zahl ohne Deckung waere
/// schlechter als eine Luecke.
/// </summary>
public static partial class PriceIndexCalculator
{
    public const int MinMatchedItems = 3;

    // Ein Preisverhaeltnis ausserhalb dieser Grenzen ist fast sicher ein falsch gelesener Beleg (Komma verrutscht,
    // andere Packung) und keine Teuerung.
    private const decimal MinRatio = 0.4m, MaxRatio = 2.5m;

    private const int MaxQuarterGap = 4;

    public static IReadOnlyList<PriceIndexPoint> Chain(IEnumerable<PriceObservation> observations)
    {
        var quarters = observations
            .Where(o => o.UnitPrice > 0m)
            .GroupBy(o => QuarterNumber(o.Date))
            .OrderBy(g => g.Key)
            .Select(g => (Number: g.Key, Items: g.GroupBy(o => o.ItemKey).ToDictionary(
                i => i.Key, i => (Price: Median(i.Select(o => o.UnitPrice)), Spend: i.Sum(o => o.Spend)))))
            .ToList();

        var index = new Dictionary<int, (decimal Value, int Matched)>();
        for (var current = 1; current < quarters.Count; current++)
        {
            // Bevorzugt ein Quartal, das schon einen Wert hat; vor dem allerersten Punkt wird das erste
            // brauchbare Vergleichsquartal zur Basis (= 100).
            for (var earlier = current - 1; earlier >= 0 && quarters[current].Number - quarters[earlier].Number <= MaxQuarterGap; earlier--)
            {
                if (index.Count > 0 && !index.ContainsKey(quarters[earlier].Number)) continue;
                var (factor, matched) = Link(quarters[earlier].Items, quarters[current].Items);
                if (matched < MinMatchedItems) continue;
                if (index.Count == 0) index[quarters[earlier].Number] = (100m, matched);
                index[quarters[current].Number] = (index[quarters[earlier].Number].Value * factor, matched);
                break;
            }
        }

        return index.OrderBy(pair => pair.Key)
            .Select(pair => new PriceIndexPoint(Label(pair.Key), StartOf(pair.Key), Math.Round(pair.Value.Value, 2, MidpointRounding.AwayFromZero), pair.Value.Matched))
            .ToList();
    }

    private static (decimal Factor, int Matched) Link(
        Dictionary<string, (decimal Price, decimal Spend)> before, Dictionary<string, (decimal Price, decimal Spend)> after)
    {
        double weighted = 0, weights = 0;
        var matched = 0;
        foreach (var (key, was) in before)
        {
            if (!after.TryGetValue(key, out var now)) continue;
            var ratio = now.Price / was.Price;
            if (ratio is < MinRatio or > MaxRatio) continue;
            var weight = (double)Math.Max(was.Spend, 0.01m);
            weighted += weight * Math.Log((double)ratio);
            weights += weight;
            matched++;
        }
        return matched == 0 ? (1m, 0) : ((decimal)Math.Exp(weighted / weights), matched);
    }

    /// <summary>
    /// Schluessel eines Artikels: Geschaeft (erstes Wort), Name und Packung - dasselbe Ding im selben Laden. Die Packung
    /// gehoert dazu, seit sie nicht mehr im Namen steht: "Knoblauch 200 g" und "Knoblauch 500 g" sind verschiedene Preise.
    /// </summary>
    public static string ItemKey(string merchant, Guid? productId, string name, decimal? packageQuantity = null, string? packageUnit = null) =>
        $"{FirstWord(merchant)}|{(productId?.ToString("N") ?? Normalize(name))}" + (packageQuantity is > 0m ? $"|{packageQuantity:0.###}{packageUnit}" : "");

    private static string FirstWord(string value) => LetterRun().Match(value ?? string.Empty).Value.ToLowerInvariant();

    private static string Normalize(string name) => Whitespace().Replace(NotLetterOrDigit().Replace((name ?? string.Empty).ToLowerInvariant(), " "), " ").Trim();

    private static int QuarterNumber(DateOnly date) => date.Year * 4 + (date.Month - 1) / 3;

    private static string Label(int number) => $"{number / 4}-Q{number % 4 + 1}";

    private static DateOnly StartOf(int number) => new(number / 4, number % 4 * 3 + 1, 1);

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2m;
    }

    [GeneratedRegex(@"\p{L}+")] private static partial Regex LetterRun();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")] private static partial Regex NotLetterOrDigit();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
