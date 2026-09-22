using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Eine Gruppe von Routen, die niemand registriert (#177).
///
/// <see cref="RouteReachabilityTests"/> haelt fest, welche Route kein Frontend aufruft - aber es liest
/// die Routenflaeche, und in der steht nur, was tatsaechlich gemappt wurde. Eine ganze Datei voller
/// Endpunkte, deren <c>Map…Endpoints()</c> nirgends aufgerufen wird, ist dort unsichtbar: kein Test
/// sieht sie, kein Aufrufer vermisst sie, und der Compiler ist zufrieden, weil die Methode ja
/// existiert.
///
/// Zwei gab es. <c>PurchaseEndpoints</c> war eine zweite, raumlose Kauf-API ganz ohne Rechtepruefung -
/// <c>store.GetAsync(id)</c> auf die blosse Id - und eine einzige <c>Map</c>-Zeile haette jeden Kauf
/// der Instanz fuer jeden Angemeldeten geoeffnet. Sie ist weg, samt ihrem Store. Die zweite steht in
/// der Ausnahmeliste, weil sie eine fertige Funktion ist und kein Versehen: was mit ihr geschieht, ist
/// eine Entscheidung und kein Aufraeumen.
/// </summary>
public sealed class EndpointRegistrationTests
{
    [Fact]
    public void Every_endpoint_group_is_registered_somewhere()
    {
        var sources = Sources();
        var unregistered = Definitions(sources)
            .Where(definition => !sources.Any(file =>
                file.Path != definition.Path &&
                file.Text.Contains($".{definition.Name}()", StringComparison.Ordinal)))
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);

        var recorded = Recorded();
        var added = unregistered.Except(recorded).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0,
            "Diese Endpunktgruppen ruft niemand auf - die Routen darin existieren nur im Quelltext, " +
            "nicht in der Anwendung. Entweder werden sie registriert, oder sie kommen mit Begruendung " +
            "in endpoint-groups-without-registration.txt:\n  " + string.Join("\n  ", added));
    }

    [Fact]
    public void A_group_that_got_registered_leaves_the_list()
    {
        var sources = Sources();
        var unregistered = Definitions(sources)
            .Where(definition => !sources.Any(file =>
                file.Path != definition.Path &&
                file.Text.Contains($".{definition.Name}()", StringComparison.Ordinal)))
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);

        var resolved = Recorded().Except(unregistered).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.True(resolved.Length == 0,
            "Diese Endpunktgruppen sind inzwischen registriert und gehoeren aus " +
            "endpoint-groups-without-registration.txt heraus:\n  " + string.Join("\n  ", resolved));
    }

    private static IEnumerable<(string Name, string Path)> Definitions(IReadOnlyList<(string Path, string Text)> sources) =>
        sources.SelectMany(file => Regex
            .Matches(file.Text, @"public static IEndpointRouteBuilder (\w+)\(\s*this IEndpointRouteBuilder")
            .Select(match => (match.Groups[1].Value, file.Path)));

    private static IReadOnlyList<(string Path, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", "FullWorth.Backend"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (path, File.ReadAllText(path)))
            .ToArray();

    private static HashSet<string> Recorded() =>
        File.ReadAllLines(Path.Combine(Root(), "tests", "FullWorth.Backend.Tests", "Architecture", "endpoint-groups-without-registration.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
