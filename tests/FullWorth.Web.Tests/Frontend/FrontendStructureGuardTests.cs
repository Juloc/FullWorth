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
    /// Sie sind der Ort, an dem etwas landet, wenn niemand entscheidet, wohin es gehört. Die vier,
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
    /// Ein Modul reicht nichts weiter, was es selbst benutzt.
    ///
    /// features/ux-kit.js hatte "export { esc } from '../core/html.js';" und rief esc zwei Zeilen
    /// später selbst auf. Ein reines Weiterreichen holt den Namen aber NICHT in den Gültigkeitsbereich
    /// des Moduls — jede Seite, die eine Abschnittskarte zeichnet, brach mit "esc is not defined".
    ///
    /// Kein Test hat das gemeldet: sie lesen Quelltext, und der Quelltext sah richtig aus. Gefunden
    /// hat es die Browserkonsole. Dieser hier findet es beim nächsten Mal vorher.
    /// </summary>
    [Fact]
    public void A_module_does_not_pass_on_what_it_uses_itself()
    {
        var offenders = new List<string>();

        foreach (var path in Scripts("app", "components", "core", "features", "pages"))
        {
            var code = Code(path);
            foreach (Match match in Regex.Matches(code, @"(?m)^export \{ ([^}]+) \} from '[^']+';"))
            {
                foreach (var name in match.Groups[1].Value.Split(',').Select(part => part.Trim().Split(' ')[0]))
                {
                    var used = Regex.Matches(code, @"(?<![\w.$])" + Regex.Escape(name) + @"\s*\(").Count;
                    if (used > 0) offenders.Add($"{Relative(path)}: {name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Diese Module reichen einen Namen weiter, den sie selbst aufrufen — dabei kommt er nie in "
            + "ihren Gültigkeitsbereich. Importieren UND exportieren:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Eine Seite bringt keinen eigenen Kopf mit.
    ///
    /// Hier stand einmal ein eigener Wächter dafür — ImportPageStylesheetGuardTests —, weil drei
    /// Seiten ihren &lt;head&gt; von Hand schrieben und dabei nur app.css luden: jedes Token löste zu
    /// nichts auf und die responsive Schicht fehlte ganz, also bekam ein Telefon unformatierte 20px
    /// große Formularfelder. Der Wächter zählte die acht Stylesheets in der richtigen Reihenfolge ab.
    ///
    /// Das Problem gibt es nicht mehr, weil es den zweiten Kopf nicht mehr gibt. Was bleibt, ist die
    /// Regel, die das sicherstellt: eine page.html ist ein Ausschnitt, kein Dokument.
    /// </summary>
    [Fact]
    public void A_page_brings_no_head_of_its_own()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(WebRoot, "pages"), "page.html", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path) is var markup
                && (markup.Contains("<head", StringComparison.OrdinalIgnoreCase)
                 || markup.Contains("<html", StringComparison.OrdinalIgnoreCase)
                 || markup.Contains("<link", StringComparison.OrdinalIgnoreCase)
                 || markup.Contains("<script", StringComparison.OrdinalIgnoreCase)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Seiten bringen einen eigenen Kopf mit:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
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
