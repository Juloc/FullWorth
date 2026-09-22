using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Welche Route niemand aufruft - als Ratsche, nicht als Bestandsaufnahme (#177).
///
/// „Fertig gebaut, aber unerreichbar" ist in diesem Haus die haeufigste Fehlerart. Ein Durchlauf ueber
/// die Routenflaeche gegen das gesamte Frontend fand 39 solche Routen, darunter die Sammelbearbeitung
/// von Buchungen (716 Zeilen) und die freie Auswertung. Das ist nicht in einem Zug zu beheben - aber
/// es darf nicht weiter wachsen.
///
/// Deshalb dieselbe Bauart wie <c>layer-violations.txt</c> und <c>module-named-files.txt</c>: die Liste
/// steht in einer Datei, sie darf nur kuerzer werden, und eine neue Zeile darin ist eine Entscheidung
/// statt eines Versehens.
///
/// Zwei Richtungen, und die zweite ist die wichtigere:
///
/// <list type="number">
///   <item>Eine NEUE Route ohne Aufrufer laesst den Test rot werden. Entweder bekommt sie eine
///         Oberflaeche, oder sie kommt mit Begruendung in die Liste.</item>
///   <item>Eine Route, die einen Aufrufer BEKOMMEN hat, muss aus der Liste heraus. Sonst verrottet
///         sie dort und die Zahl sagt bald nichts mehr.</item>
/// </list>
/// </summary>
public sealed class RouteReachabilityTests
{
    [Fact]
    public void No_new_route_is_built_without_a_way_to_reach_it()
    {
        var unreachable = Unreachable();
        var recorded = Recorded();

        var added = unreachable.Except(recorded).OrderBy(route => route, StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0,
            "Diese Routen kann niemand erreichen und sie stehen nicht in routes-without-caller.txt. " +
            "Entweder bekommen sie eine Oberflaeche, oder sie kommen mit Begruendung in die Datei:\n  " +
            string.Join("\n  ", added));
    }

    [Fact]
    public void A_route_that_found_a_caller_leaves_the_list()
    {
        var unreachable = Unreachable();
        var recorded = Recorded();

        var resolved = recorded.Except(unreachable).OrderBy(route => route, StringComparer.Ordinal).ToArray();
        Assert.True(resolved.Length == 0,
            "Diese Routen haben inzwischen einen Aufrufer und gehoeren aus routes-without-caller.txt heraus - " +
            "sonst verrottet die Liste und ihre Zahl sagt bald nichts mehr:\n  " +
            string.Join("\n  ", resolved));
    }

    /// <summary>
    /// Zweistufig, damit ein dynamisch zusammengesetzter Pfad nicht faelschlich gemeldet wird: erst das
    /// statische Praefix der Route, dann - falls das nicht trifft - das letzte statische Segment als
    /// eigenes Wort. <c>/api/intelligence/brand-assets/{sha}</c> zum Beispiel steht nirgends im
    /// Quelltext: die Adresse kommt aus dem Katalog. Ohne die zweite Stufe waere das ein Fehlalarm.
    /// </summary>
    private static HashSet<string> Unreachable()
    {
        var frontend = Frontend();
        return Routes()
            .Where(route =>
            {
                var stem = route.Path.Split("/{")[0].TrimStart('/');
                if (frontend.Contains(stem, StringComparison.Ordinal)) return false;
                var last = stem.Split('/')[^1];
                return !Regex.IsMatch(frontend, "['`/\"]" + Regex.Escape(last) + "['`?/\"]");
            })
            .Select(route => $"{route.Method} {route.Path}")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string Method, string Path)> Routes() =>
        File.ReadAllLines(Path.Combine(Root(), "tests", "FullWorth.Backend.Tests", "Architecture", "route-surface.txt"))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[1].StartsWith("/api/", StringComparison.Ordinal))
            .Select(parts => (parts[0], parts[1]));

    private static HashSet<string> Recorded() =>
        File.ReadAllLines(Path.Combine(Root(), "tests", "FullWorth.Backend.Tests", "Architecture", "routes-without-caller.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    private static string Frontend()
    {
        var root = Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot");
        var builder = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            if (file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                builder.Append(File.ReadAllText(file));
        return builder.ToString();
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
