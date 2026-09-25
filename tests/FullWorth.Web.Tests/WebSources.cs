namespace FullWorth.Web.Tests;

/// <summary>
/// Die Quelldateien des Frontends, aus dem Arbeitsverzeichnis gelesen.
///
/// Warum das hier steht und nicht in jeder Testdatei noch einmal: seit #154 liegt das Markup einer
/// Seite nicht mehr unter <c>wwwroot/pages/…/page.html</c>, sondern als Razor-Seite unter
/// <c>Pages/…/Index.cshtml</c>. Beim Umzug der ersten elf Seiten scheiterten vier Tests an nichts
/// anderem als an diesem Pfad - jeder hatte seinen eigenen kleinen Leser, und jeder musste einzeln
/// nachgezogen werden. Ein Ort dafür genügt.
/// </summary>
public static class WebSources
{
    /// <summary>
    /// Das Markup einer Seite, z. B. <c>Page("Pension")</c> oder <c>Page("Settings/Intelligence")</c>.
    /// </summary>
    public static string Page(string name) =>
        File.ReadAllText(Path.Combine(
            new[] { Web(), "Pages" }.Concat(name.Split('/')).Append("Index.cshtml").ToArray()));

    /// <summary>Eine Datei unter <c>wwwroot</c>, z. B. <c>Asset("pages", "pension", "entry.js")</c>.</summary>
    public static string Asset(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Web(), "wwwroot" }.Concat(parts).ToArray()));

    /// <summary>
    /// Der Rahmen, den jede Seite teilt: Kopf, Stilkette, Topbar, Coach-Dock, Toast.
    ///
    /// Das stand bis zum Ende von #154 in <c>wwwroot/index.html</c>. Das Dokument gibt es nicht mehr —
    /// was darin für ALLE Seiten galt, steht jetzt hier, und was nur eine Seite betraf, bei ihr.
    /// </summary>
    public static string Layout() => Shared("_Layout.cshtml");

    /// <summary>Die Seitenleiste. Trug in der alten Hülle die erzeugte <c>nav</c>-Sektion.</summary>
    public static string Navigation() => Shared("_Navigation.cshtml");

    /// <summary>Die untere Leiste am Telefon.</summary>
    public static string BottomNavigation() => Shared("_BottomNavigation.cshtml");

    /// <summary>Eine Quelldatei des Web-Projekts, z. B. <c>Source("Navigation", "PageHeadings.cs")</c>.</summary>
    public static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Web() }.Concat(parts).ToArray()));

    private static string Shared(string name) =>
        File.ReadAllText(Path.Combine(Web(), "Pages", "Shared", name));

    /// <summary>
    /// Was ein Markup laedt: seine Module und Stilblaetter und alles, was diese Module importieren -
    /// statisch wie dynamisch, denn ein dynamisch importiertes Modul wird gebraucht, sobald sein Pfad laeuft.
    /// </summary>
    /// <remarks>
    /// Klassische Skripte zaehlen mit, und ebenso absolute Importe: app/boot.js laeuft als klassisches
    /// Skript im Kopf und holt `/app/appearance.js` mit `import()`. Kannte der Graph beides nicht, sah
    /// er auch nicht, dass nach dem Ende von index.html drei Module von niemandem mehr geladen wurden.
    /// </remarks>
    public static HashSet<string> Reachable(string markup)
    {
        var queue = new Queue<string>(System.Text.RegularExpressions.Regex
            .Matches(markup, """<script[^>]+src="~?(?<path>/[^"?#]+)""")
            .Select(match => match.Groups["path"].Value)
            .Concat(System.Text.RegularExpressions.Regex
                .Matches(markup, """<link[^>]+rel="stylesheet"[^>]+href="~?(?<path>/[^"?#]+)""")
                .Select(match => match.Groups["path"].Value)));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (!seen.Add(path)) continue;
            var file = Path.Combine(Web(), "wwwroot", path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) continue;
            foreach (var specifier in System.Text.RegularExpressions.Regex
                         .Matches(File.ReadAllText(file), """(?:from|import)\s*\(?\s*['"](?<spec>(?:\.{1,2})?/[^'"]+)['"]""")
                         .Select(match => match.Groups["spec"].Value))
            {
                var resolved = specifier.StartsWith('/') ? specifier : Resolve(path, specifier);
                if (resolved is not null) queue.Enqueue(resolved);
            }
        }
        return seen;
    }

    /// <summary>
    /// Ob irgendeine Seite dieses Asset laedt. Seit #154 ist das die Frage, die "steht es im Vorrat des
    /// Service Workers" einmal meinte: gehoert das Modul zur Anwendung, oder liegt es nur herum? Der
    /// Vorrat haelt keine Seiten mehr (Abschnitt 12); eine Seite legt ihre Module beim ersten Besuch
    /// selbst in den Cache. Diese Pruefung ist die strengere - sie faengt auch ein Modul, das zwar
    /// gelistet, aber von nichts importiert war.
    /// </summary>
    public static bool LoadedByAPage(string path) => PageLoadGraph.Value.Contains(path);

    private static readonly Lazy<HashSet<string>> PageLoadGraph = new(() =>
        Directory.EnumerateFiles(Path.Combine(Web(), "Pages"), "Index.cshtml", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Append(Layout() + Navigation() + BottomNavigation())
            .SelectMany(Reachable)
            .ToHashSet(StringComparer.Ordinal));

    /// <summary>Loest einen relativen Import gegen das Verzeichnis des importierenden Moduls auf.</summary>
    private static string? Resolve(string importer, string specifier)
    {
        var segments = new List<string>(importer.TrimStart('/').Split('/'));
        segments.RemoveAt(segments.Count - 1);
        foreach (var segment in specifier.Split('/'))
        {
            if (segment is "." or "") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return '/' + string.Join('/', segments);
    }

    /// <summary>
    /// Die Wurzel des Arbeitsbaums. Ein Test, der ueber das Web-Projekt hinaussieht - etwa in die
    /// aufgezeichnete Routenflaeche -, braucht sie; sie noch einmal zu suchen waere der zweite Leser,
    /// den diese Klasse gerade abschafft.
    /// </summary>
    public static string RepoRoot => Root();

    private static string Web() => Path.Combine(Root(), "src", "FullWorth.Web");

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("FullWorth.slnx nicht gefunden.");
        return directory.FullName;
    }
}
