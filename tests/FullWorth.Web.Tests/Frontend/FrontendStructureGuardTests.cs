using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// Die Regeln, die die Struktur tragen. Es sind wenige, und jede hält etwas fest, das sonst still
/// zurückfällt — nicht durch bösen Willen, sondern weil die Abkürzung im Moment immer billiger ist.
///
/// Sie stehen auch in CLAUDE.md. Dieser Test ist die Fassung, die widerspricht.
/// </summary>
public sealed class FrontendStructureGuardTests
{
    private static readonly string WebRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "FullWorth.Web", "wwwroot"));

    private static IEnumerable<string> Scripts(params string[] folders) =>
        folders.Select(folder => Path.Combine(WebRoot, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.js", SearchOption.AllDirectories));

    private static string Relative(string path) =>
        Path.GetRelativePath(WebRoot, path).Replace('\\', '/');

    /// <summary>
    /// Der Quelltext ohne Kommentare. Ein Wächter, der Prosa liest, meldet den Satz "The document
    /// import (step 2) lives in ..." als Verstoß — und das hat er getan.
    /// </summary>
    private static string Code(string path) =>
        Regex.Replace(Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"(?m)^\s*//.*$", string.Empty);

    /// <summary>
    /// Kein Stylesheet wird nach dem Zeichnen nachgeladen.
    ///
    /// Das war die zweitgrößte Sprungquelle der Anwendung: sechzehn Module hängten ihr eigenes
    /// &lt;link&gt; beim ersten Besuch ihrer Ansicht an den Kopf, also zeichnete jede Ansicht einmal
    /// ungestylt und baute sich dann um.
    /// </summary>
    [Fact]
    public void No_module_appends_a_stylesheet()
    {
        var offenders = Scripts("app", "components", "core", "features", "pages", "security", "passkeys", "push", "pwa")
            .Where(path => !Relative(path).Equals("sw.js", StringComparison.Ordinal))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"rel\s*=\s*['""]stylesheet['""]"))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Module laden ein Stylesheet nach. Es gehört als <link> ins Dokument:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Kein Modul wird zur Laufzeit nachgeladen.
    ///
    /// Ein import() heißt: beim ersten Besuch ist die Seite noch nicht da, sondern kommt gleich. Genau
    /// das soll es nicht geben — alles ist beim ersten Zeichnen da oder es gehört nicht dazu.
    ///
    /// Zwei Ausnahmen, beide begründet: app/boot.js lädt das Aussehen erst, wenn das Dokument steht
    /// (es ist ein klassisches Skript im Kopf und kann gar nicht anders), und pwa/register-sw.js holt
    /// die Coach-Erweiterung, die in Block 4 eine gewöhnliche Seite wird.
    /// </summary>
    [Fact]
    public void No_module_is_loaded_at_runtime()
    {
        var allowed = new[] { "app/boot.js", "pwa/register-sw.js" };

        var offenders = Scripts("app", "components", "core", "features", "pages")
            .Where(path => !allowed.Contains(Relative(path), StringComparer.Ordinal))
            .Where(path => Regex.IsMatch(Code(path), @"(?<![\w.$])import\s*\("))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Module laden zur Laufzeit nach:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Eine Komponente kennt keine Seite und keinen Server.
    ///
    /// Sobald eine Komponente weiß, woher ihre Daten kommen, ist sie keine Komponente mehr, sondern
    /// ein Stück dieser einen Seite — und die nächste Seite baut sich ihre eigene.
    /// </summary>
    [Fact]
    public void A_component_knows_neither_a_page_nor_the_server()
    {
        var offenders = Scripts("components")
            .Select(path => (Path: Relative(path), Source: File.ReadAllText(path)))
            .Where(file => file.Source.Contains("../pages/", StringComparison.Ordinal)
                        || file.Source.Contains("../features/", StringComparison.Ordinal)
                        || Regex.IsMatch(file.Source, @"['""]/?(api|bff)/"))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Komponenten kennen eine Seite oder den Server:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Eine Seite kennt keine andere Seite.
    ///
    /// Was zwei Seiten brauchen, gehört nach components/ oder core/. Ein Verweis von Seite zu Seite
    /// ist der erste Schritt zu einem Knäuel, das man später nicht mehr auseinanderzieht.
    /// </summary>
    [Fact]
    public void A_page_does_not_reach_into_another_page()
    {
        var offenders = new List<string>();

        foreach (var path in Scripts("pages"))
        {
            var own = Path.GetDirectoryName(Relative(path))!.Replace('\\', '/');
            foreach (Match match in Regex.Matches(File.ReadAllText(path), @"from\s+'([^']*pages/[^']+)'"))
            {
                var target = match.Groups[1].Value;
                if (!target.Contains(own, StringComparison.Ordinal)) offenders.Add($"{Relative(path)} -> {target}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Diese Seiten greifen in eine andere:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Keine Stylesheets in der Wurzel.
    ///
    /// Sie sind der Ort, an dem etwas landet, wenn niemand entscheidet, wohin es gehört. Die sechs,
    /// die noch dort liegen, stehen hier namentlich — die Liste darf kürzer werden, nie länger.
    /// </summary>
    [Fact]
    public void The_root_collects_no_new_stylesheets()
    {
        string[] known = ["app.css", "appearance.css", "design-depth.css", "dialogs.css"];

        var found = Directory.EnumerateFiles(WebRoot, "*.css").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var extra = found.Except(known, StringComparer.Ordinal).ToArray();

        Assert.True(extra.Length == 0,
            "Neue Stylesheets in der Wurzel. Sie gehören nach styles/, zu ihrer Seite oder zu ihrer Komponente:"
            + Environment.NewLine + string.Join(Environment.NewLine, extra!));
    }

    /// <summary>
    /// Eine Seite ist ein Ordner mit page.html, und index.html ist erzeugt, nicht gepflegt.
    /// </summary>
    [Fact]
    public void Every_page_folder_is_a_page()
    {
        var pages = Path.Combine(WebRoot, "pages");
        Assert.True(Directory.Exists(pages));

        var html = File.ReadAllText(Path.Combine(WebRoot, "index.html"));
        foreach (var folder in Directory.EnumerateDirectories(pages, "*", SearchOption.AllDirectories))
        {
            var page = Path.Combine(folder, "page.html");
            // Ein Zwischenordner (pages/settings/security) trägt selbst keine Seite - er trägt welche.
            if (!File.Exists(page)) continue;

            var id = Regex.Match(File.ReadAllText(page), @"id=""(view-[a-z-]+)""");
            Assert.True(id.Success, $"{Relative(page)} hat keinen Ansichtsnamen.");
            Assert.Contains($"id=\"{id.Groups[1].Value}\"", html);
        }
    }
}
