using System.Globalization;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>Was an einem Positionsnamen oder einer Mengenangabe eine Packung beschreibt.</summary>
/// <param name="Count">Wie viele Packungen der Groesse in einer verkauften Einheit stecken ("2 x 100 g" -> 2); sonst null.</param>
/// <param name="Name">Der Name ohne die Packungsangabe; null, wenn die Angabe nicht im Namen stand.</param>
public sealed record PackSizeReading(decimal? Count, decimal Quantity, string Unit, string? Name);

/// <summary>
/// Liest Packungsgroessen, wie sie auf deutschen Belegen stehen: "Knoblauch 200 g", "Eier, 10er",
/// "Joghurt 5x55g", "0,5 l". Die Groesse gehoert an die Position, nicht in den Namen - erst dann
/// lassen sich "Knoblauch 200 g" und "Knoblauch" als dieselbe Ware ueber die Packung vergleichen.
///
/// Gelesen wird nur, was eindeutig eine Packung ist: am Ende des Namens oder als "10er" darin, und
/// eine Mengenangabe, die nur aus der Packung besteht. "Super 95" oder "3,5 %" sind keine.
/// </summary>
public static partial class PackSize
{
    // "2 x 100 g", "5x55g", "20 × 30 g"
    [GeneratedRegex(@"(?<count>\d{1,3})\s*[x×]\s*(?<size>\d{1,4}(?:[.,]\d{1,3})?)\s*(?<unit>kg|g|ml|cl|l)(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex Multi();

    // "200 g", "1 kg", "0,5 l", "330ml"
    [GeneratedRegex(@"(?<size>\d{1,4}(?:[.,]\d{1,3})?)\s*(?<unit>kg|g|ml|cl|l)(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex Sized();

    // "10er", "6er Chicken McNuggets"
    [GeneratedRegex(@"(?<![\p{L}\d])(?<size>\d{1,3})\s*er(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex Dozen();

    // "10 Stk", "50 Stück"
    [GeneratedRegex(@"(?<size>\d{1,3})\s*(?:stk\.?|st\.?|stück|stueck)(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex Pieces();

    /// <summary>
    /// Was an einer Position gilt: eine gesetzte Packung bleibt, wie sie ist (der Benutzer oder die Auslese
    /// wussten es besser); sonst wird sie aus der Mengenangabe, dann aus dem Namen gelesen.
    /// </summary>
    public static (string Name, string? QuantityUnit, decimal? Quantity, string? Unit, decimal? Count) Resolve(
        string name, string? quantityUnit, decimal? packageQuantity, string? packageUnit, decimal? packageCount)
    {
        if (packageQuantity is > 0m) return (name, quantityUnit, packageQuantity, packageUnit, packageCount);
        if (FromQuantityUnit(quantityUnit) is { } fromUnit) return (name, "piece", fromUnit.Quantity, fromUnit.Unit, fromUnit.Count);
        if (FromName(name) is { } fromName) return (fromName.Name!, quantityUnit, fromName.Quantity, fromName.Unit, fromName.Count);
        return (name, quantityUnit, packageQuantity, packageUnit, packageCount);
    }

    /// <summary>Die Packung aus einem Positionsnamen; null, wenn er keine nennt.</summary>
    public static PackSizeReading? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Trim();

        // Die Packung steht hinten ("Knoblauch 200 g", "Eier, Bodenhaltung, 10er") ...
        foreach (var (pattern, multi, pieces) in new[] { (Multi(), true, false), (Sized(), false, false), (Pieces(), false, true), (Dozen(), false, true) })
        {
            var hit = pattern.Matches(text).LastOrDefault();
            if (hit is null || !EndsAt(text, hit)) continue;
            var reading = Read(hit, multi, pieces);
            if (reading is null) continue;
            var rest = text[..hit.Index].TrimEnd(' ', ',', '-', '/', '(');
            return rest.Length >= 2 ? reading with { Name = rest } : null;
        }

        // ... oder vorn ("6er Chicken McNuggets"): die Zahl bleibt dort stehen, nur "er" ist die Angabe.
        var front = Dozen().Match(text);
        if (front.Success && front.Index == 0)
        {
            var reading = Read(front, false, true);
            var rest = text[front.Length..].TrimStart(' ', ',', '-');
            if (reading is not null && rest.Length >= 2) return reading with { Name = rest };
        }
        return null;
    }

    /// <summary>Die Packung aus einer Mengenangabe wie "500 g", "2 x 100 g", "0,5 l"; nur wenn sie nichts sonst enthaelt.</summary>
    public static PackSizeReading? FromQuantityUnit(string? quantityUnit)
    {
        if (string.IsNullOrWhiteSpace(quantityUnit)) return null;
        var text = quantityUnit.Trim();
        foreach (var (pattern, multi, pieces) in new[] { (Multi(), true, false), (Sized(), false, false), (Pieces(), false, true) })
        {
            var hit = pattern.Match(text);
            if (!hit.Success || hit.Index != 0 || hit.Length != text.Length) continue;
            var reading = Read(hit, multi, pieces);
            if (reading is not null) return reading with { Name = null };
        }
        return null;
    }

    private static bool EndsAt(string text, Match hit) =>
        text[(hit.Index + hit.Length)..].Trim(' ', ',', ')', '.').Length == 0;

    private static PackSizeReading? Read(Match hit, bool multi, bool pieces)
    {
        if (!decimal.TryParse(hit.Groups["size"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var size) || size <= 0m)
            return null;
        if (pieces) return new PackSizeReading(null, size, "piece", null);
        var unit = hit.Groups["unit"].Value.ToLowerInvariant();
        // cl kennt FullWorth nicht als Einheit; 33 cl sind 330 ml.
        if (unit == "cl") { size *= 10m; unit = "ml"; }
        decimal? count = null;
        if (multi)
        {
            count = decimal.Parse(hit.Groups["count"].Value, CultureInfo.InvariantCulture);
            if (count <= 1m) count = null;
        }
        return new PackSizeReading(count, size, unit, null);
    }
}
