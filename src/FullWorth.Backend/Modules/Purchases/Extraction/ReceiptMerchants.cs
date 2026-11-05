using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Purchases.Extraction;

/// <summary>
/// The merchant of a receipt. A known chain is recognised by its name anywhere in the text - the
/// header is the most damaged part of a scan - and gets one canonical spelling; any other merchant is
/// the first header line that looks like a name.
/// </summary>
internal static partial class ReceiptMerchants
{
    private static readonly (Regex Pattern, string Name)[] Chains =
    [
        (Chain("penny"), "Penny"), (Chain("rewe"), "REWE"), (Chain("kaufland"), "Kaufland"),
        (Chain("ikea"), "IKEA"), (Chain("aldi"), "ALDI"), (Chain("lidl"), "Lidl"), (Chain("edeka"), "EDEKA"),
        (Chain("netto"), "Netto"), (Chain("rossmann"), "Rossmann"), (Chain("dm[- ]drogerie"), "dm"),
        (Chain("obi"), "OBI"), (Chain("tedi"), "TEDi"), (Chain("bijou brigitte"), "Bijou Brigitte"),
        (Chain("mcdonald"), "McDonald's"), (Chain("burger king"), "Burger King"),
        (Chain("esso"), "Esso"), (Chain("aral"), "Aral"), (Chain("shell"), "Shell"), (Chain("jet"), "JET"),
        (Chain("hornbach"), "Hornbach"), (Chain("müller"), "Müller"), (Chain("action"), "Action"),
        (Chain("h&m|h & m|hennes"), "H&M"), (Chain("deichmann"), "Deichmann"), (Chain("apotheke"), "Apotheke")
    ];

    /// <summary>The first chains in <see cref="Chains"/> (the large grocers) may be recognised anywhere in the text.</summary>
    private const int BodyChains = 8;

    public static string? Find(IReadOnlyList<string> lines)
    {
        var header = string.Join('\n', lines.Take(10));
        foreach (var (pattern, name) in Chains)
            if (pattern.IsMatch(header)) return name;

        // Further down only names that cannot be a product word ("Jet", "Shell") count.
        var text = string.Join('\n', lines);
        foreach (var (pattern, name) in Chains.Take(BodyChains))
            if (pattern.IsMatch(text)) return name;

        return lines.Take(8).FirstOrDefault(line => NameLikeRegex().IsMatch(line));
    }

    private static Regex Chain(string name) =>
        new($@"(?<!\p{{L}})(?:{name})(?!\p{{L}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // At least two real words of letters, no amount and no receipt furniture.
    [GeneratedRegex(@"^(?!.*\d[,.]\d{2})(?!.*(?:kundenbeleg|eur|uid|tel))\p{L}{3,}", RegexOptions.IgnoreCase)]
    private static partial Regex NameLikeRegex();
}
