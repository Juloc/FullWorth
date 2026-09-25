using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Architecture;

/// <summary>
/// Vorbereitung fuer Issue #162 (die seitenweise CSS-Ueberarbeitung, Scheibe 11-13): eine rohe
/// Pixelzahl in <c>padding</c>/<c>margin</c>/<c>gap</c>/<c>border-radius</c> soll irgendwann ein
/// Abstands- bzw. Rundungs-Token aus <c>styles/tokens.css</c> sein statt einer Zahl, die zufaellig zur
/// Nachbarregel passt. Diese Scheibe korrigiert noch NICHTS davon - sie friert nur die heutige Zahl je
/// Datei ein, damit Scheibe 11-13 eine Messlatte hat, die nur sinken darf. Die Tabelle unten wurde
/// erzeugt, nicht von Hand getippt: ein kleines Node-Skript zaehlt in jeder <c>styles/*.css</c>- und
/// jeder <c>pages/**/page.css</c>-Datei die <c>px</c>-Werte in genau diesen vier Deklarationsarten
/// (siehe <see cref="AktuelleZaehlung"/> fuer dieselbe Zaehlung als C#-Gegenstueck - beide muessen
/// uebereinstimmen, sonst waere die eingefrorene Zahl nicht die, die der Test tatsaechlich misst).
///
/// Reproduktion: <c>node</c> ueber alle <c>styles/*.css</c> (nur die Datei-Ebene, nicht rekursiv - so
/// wie es im Auftrag stand) und alle <c>pages/**/page.css</c>, Kommentare vorher entfernt, je Datei die
/// Regex <c>(?:^|[;{])\s*((?:padding|margin|gap|row-gap|column-gap|border-radius)[a-zA-Z-]*)\s*:\s*([^;{}]+)</c>
/// fuer die Deklaration und <c>-?\d+(?:\.\d+)?px\b</c> fuer die Pixelwerte in deren rechter Seite.
/// </summary>
public sealed class SpacingRatchetTests
{
    /// <summary>
    /// Eingefroren am 2026-09-17 (Scheibe 10 von #157/#162-Vorbereitung). Nur diese Datei senkt Zahlen -
    /// Scheibe 11-13 tut das durch echte Migration auf Tokens, nicht durch Anheben dieser Tabelle.
    /// </summary>
    private static readonly Dictionary<string, int> KnownRawPixelCounts = new()
    {
        ["pages/accounts/page.css"] = 60,
        ["pages/admin/page.css"] = 16,
        ["pages/analytics/page.css"] = 19,
        ["pages/audit/page.css"] = 2,
        ["pages/budgets/page.css"] = 5,
        ["pages/categories/page.css"] = 3,
        ["styles/coach.css"] = 60,
        ["pages/collections/page.css"] = 2,
        ["pages/compensation/page.css"] = 228,
        ["pages/contracts/page.css"] = 103,
        ["pages/dashboard/page.css"] = 8,
        ["styles/insights.css"] = 47,
        ["pages/networth/page.css"] = 127,
        ["pages/notifications/page.css"] = 1,
        ["pages/pension/page.css"] = 9,
        ["pages/purchases/page.css"] = 221,
        ["pages/rules/page.css"] = 11,
        ["pages/settings/import/finanzguru/xlsx/page.css"] = 30,
        ["pages/settings/import/page.css"] = 26,
        ["pages/settings/intelligence/page.css"] = 58,
        ["pages/settings/page.css"] = 3,
        ["pages/tax/page.css"] = 42,
        ["pages/transactions/page.css"] = 40,
        ["styles/app.css"] = 95,
        ["styles/bank-connections.css"] = 10,
        ["styles/investment-performance.css"] = 48,
        ["styles/components.css"] = 20,
        ["styles/design-depth.css"] = 8,
        ["styles/dialogs.css"] = 30,
        ["styles/responsive.css"] = 39,
        ["styles/shell.css"] = 22
    };

    private static readonly Regex PropertyDeclaration = new(
        @"(?:^|[;{])\s*((?:padding|margin|gap|row-gap|column-gap|border-radius)[a-zA-Z-]*)\s*:\s*([^;{}]+)",
        RegexOptions.Compiled);

    private static readonly Regex PxValue = new(@"-?\d+(?:\.\d+)?px\b", RegexOptions.Compiled);
    private static readonly Regex CssComment = new(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);

    [Fact]
    public void No_file_exceeds_its_frozen_raw_px_ceiling()
    {
        var current = AktuelleZaehlung();

        var appeared = current.Keys.Where(x => !KnownRawPixelCounts.ContainsKey(x))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(appeared.Count == 0,
            "Neue Datei mit roher px-Zahl in padding/margin/gap/border-radius - fuege einen "
            + "Eintrag mit der heutigen Zahl hinzu (diese Scheibe senkt nichts, das ist Aufgabe von "
            + "#162): " + string.Join(", ", appeared.Select(x => $"{x} ({current[x]})")));

        var worse = current
            .Where(x => KnownRawPixelCounts.TryGetValue(x.Key, out var known) && x.Value > known)
            .Select(x => $"{x.Key}: {KnownRawPixelCounts[x.Key]} -> {x.Value}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(worse.Count == 0,
            "Diese Dateien haben MEHR rohe px-Werte als die eingefrorene Obergrenze erlaubt - ein "
            + "Token aus styles/tokens.css verwenden statt einer neuen Zahl: "
            + string.Join(", ", worse));
    }

    /// <summary>
    /// Ratsche: sinkt eine Zahl (weil Scheibe 11-13 eine Datei auf Tokens umgestellt hat), muss die
    /// Tabelle oben mitsinken - sonst stuende dort eine Obergrenze, die niemand mehr erreicht.
    /// </summary>
    [Fact]
    public void The_spacing_ratchet_has_no_stale_entries()
    {
        var current = AktuelleZaehlung();
        var stale = KnownRawPixelCounts.Keys
            .Where(known => !current.ContainsKey(known) || current[known] < KnownRawPixelCounts[known])
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            "Diese Dateien haben WENIGER rohe px-Werte als die eingefrorene Zahl - bitte die Zahl in "
            + "KnownRawPixelCounts senken (oder den Eintrag streichen, wenn die Datei jetzt bei 0 "
            + "steht): " + string.Join(", ", stale));
    }

    private static Dictionary<string, int> AktuelleZaehlung()
    {
        var wwwroot = WwwRoot();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        var files = new List<string>();
        files.AddRange(Directory.EnumerateFiles(Path.Combine(wwwroot, "styles"), "*.css", SearchOption.TopDirectoryOnly));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(wwwroot, "pages"), "page.css", SearchOption.AllDirectories));

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(wwwroot, file).Replace(Path.DirectorySeparatorChar, '/');
            var withoutComments = CssComment.Replace(File.ReadAllText(file), " ");

            var total = 0;
            foreach (Match match in PropertyDeclaration.Matches(withoutComments))
                total += PxValue.Matches(match.Groups[2].Value).Count;

            if (total > 0) result[relative] = total;
        }

        return result;
    }

    private static string WwwRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot");
    }
}
