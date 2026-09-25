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
    /// Der Bereich einer Datei unter pages/: pages/&lt;bereich&gt;. Unterseiten gehoeren zu ihrem Bereich -
    /// die Kontodetails zeichnet bewusst dasselbe Modul wie die Kontenliste, und die Einstellungen
    /// kennen ihre Unterseiten.
    /// </summary>
    private static string? PageOf(string relative)
    {
        var parts = relative.Split('/');
        return parts.Length > 2 && parts[0] == "pages" ? parts[0] + '/' + parts[1] : null;
    }

    /// <summary>Ein relativer Import, aufgeloest gegen den Ordner des importierenden Moduls.</summary>
    private static string Resolve(string importer, string specifier)
    {
        var segments = importer.Split('/').SkipLast(1).ToList();
        foreach (var segment in specifier.Split('/'))
        {
            if (segment is "." or "") continue;
            if (segment == "..") segments.RemoveAt(segments.Count - 1);
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }

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
    /// Eine Ausnahme, und sie ist begründet: app/boot.js lädt das Aussehen erst, wenn das Dokument
    /// steht — es ist ein klassisches Skript im Kopf und kann gar nicht anders. Die zweite hieß
    /// pwa/register-sw.js und holte den Coach nach; der ist jetzt eine Seite wie jede andere.
    /// </summary>
    [Fact]
    public void No_module_is_loaded_at_runtime()
    {
        var allowed = new[] { "app/boot.js" };

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
    /// <remarks>
    /// Die erste Fassung suchte nach "pages/" im Importpfad. Von einer Seite zur anderen schreibt man
    /// aber "../insights/page.js", und ein reiner Seiteneffekt-Import hat kein "from" - sie sah keinen
    /// einzigen der fuenf Verweise, die die Startseite und die Konten in fremde Seiten hatten. Jetzt
    /// wird jeder Import aufgeloest, und die Grenze ist der Bereich unter pages/. Was zwei Bereiche
    /// brauchen, steht in features/ (die Bankverbindung, die Einrichtung, die Hinweise, die
    /// Depot-Auswertung) - samt seinem Stylesheet unter styles/, sonst fehlt es der zweiten Seite.
    /// </remarks>
    [Fact]
    public void A_page_does_not_reach_into_another_page()
    {
        var offenders = new List<string>();

        foreach (var path in Scripts("pages"))
        {
            var own = PageOf(Relative(path));
            foreach (Match match in Regex.Matches(Code(path), @"(?:\bfrom|\bimport)\s*\(?\s*'(?<spec>\.{1,2}/[^']+)'"))
            {
                var target = Resolve(Relative(path), match.Groups["spec"].Value);
                var page = PageOf(target);
                if (page is not null && page != own) offenders.Add($"{Relative(path)} -> {target}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Diese Seiten greifen in eine andere:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Ein geteiltes Modul bringt sein Stylesheet mit: erreicht eine Seite features/x.js und gibt es
    /// styles/x.css, dann laedt die Seite es.
    ///
    /// Die Insights, die Depot-Auswertung und die Bankdialoge gestalteten sich aus dem page.css EINER
    /// Seite. Auf der zweiten Seite fehlte es - das Hinweis-Widget der Startseite traf keine einzige
    /// Regel. Kein Test sah es: der Import funktionierte, nur das Aussehen nicht.
    /// </summary>
    [Fact]
    public void A_shared_module_brings_its_stylesheet_to_every_page()
    {
        var shared = Directory.EnumerateFiles(Path.Combine(WebRoot, "features"), "*.js")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => File.Exists(Path.Combine(WebRoot, "styles", name + ".css")))
            .ToArray();
        Assert.Contains("insights", shared);

        var missing = new List<string>();
        foreach (var page in Directory.EnumerateFiles(Path.Combine(WebRoot, "..", "Pages"), "Index.cshtml", SearchOption.AllDirectories))
        {
            var loaded = WebSources.Reachable(File.ReadAllText(page) + WebSources.Layout());
            foreach (var name in shared)
                if (loaded.Contains($"/features/{name}.js") && !loaded.Contains($"/styles/{name}.css"))
                    missing.Add($"{Path.GetRelativePath(Path.Combine(WebRoot, ".."), page).Replace('\\', '/')} uses features/{name}.js without styles/{name}.css");
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// Die Klassen im Markup einer Seite sind auf dieser Seite gestaltet: steht eine Regel fuer sie in
    /// irgendeinem Stylesheet, dann in einem, das die Seite laedt.
    ///
    /// Im einen Dokument galt jedes Stylesheet ueberall. Seit jede Seite nur ihr eigenes laedt, stand die
    /// Broker-PDF-Seite ohne ihr Layout da (ihre Bausteine lagen im Stylesheet der Import-Zentrale, ihr
    /// eigener Verweis zeigte auf eine Datei, die es nie gab) und die Passkeys ohne das Raster der
    /// Einstellungen. Gemessen im Browser; dieser Test ist die statische Haelfte davon - er liest das
    /// Markup der Razor-Seiten. Was Module zur Laufzeit erzeugen, prueft er nicht.
    /// </summary>
    [Fact]
    public void A_page_loads_the_rules_for_its_own_markup()
    {
        var styledIn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var sheet in Directory.EnumerateFiles(Path.Combine(WebRoot, "styles"), "*.css", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(WebRoot, "pages"), "*.css", SearchOption.AllDirectories)))
        {
            var css = Regex.Replace(File.ReadAllText(sheet), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match match in Regex.Matches(css, @"\.(?<name>[a-zA-Z][\w-]*)"))
            {
                if (!styledIn.TryGetValue(match.Groups["name"].Value, out var set))
                    styledIn[match.Groups["name"].Value] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add("/" + Relative(sheet));
            }
        }
        Assert.Contains("/styles/components.css", styledIn["tx-marker"]);

        var unstyled = new List<string>();
        foreach (var page in Directory.EnumerateFiles(Path.Combine(WebRoot, "..", "Pages"), "Index.cshtml", SearchOption.AllDirectories))
        {
            var markup = Regex.Replace(File.ReadAllText(page), @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
            var loaded = WebSources.Reachable(markup + WebSources.Layout());
            var classes = Regex.Matches(markup, "class=\"(?<list>[^\"@{}]*)\"")
                .SelectMany(match => match.Groups["list"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.Ordinal);
            foreach (var name in classes)
                if (styledIn.TryGetValue(name, out var sheets) && !sheets.Any(loaded.Contains))
                    unstyled.Add($"{Path.GetRelativePath(Path.Combine(WebRoot, ".."), page).Replace('\\', '/')}: .{name} (only in {string.Join(", ", sheets)})");
        }

        Assert.True(unstyled.Count == 0, "these classes are styled, but not on the page that uses them:"
            + Environment.NewLine + string.Join(Environment.NewLine, unstyled));
    }

    /// <summary>
    /// Jede Datei, auf die eine Razor-Seite verweist, gibt es. Die Broker-PDF-Seite verwies seit ihrer
    /// Umstellung auf ein Stylesheet, das nie angelegt wurde - der Browser bekam eine 404, und niemand
    /// merkte es, weil die Seite ohnehin nichts davon erwartete.
    /// </summary>
    [Fact]
    public void Every_file_a_page_links_exists()
    {
        var missing = new List<string>();
        foreach (var page in Directory.EnumerateFiles(Path.Combine(WebRoot, "..", "Pages"), "*.cshtml", SearchOption.AllDirectories))
            foreach (Match match in Regex.Matches(File.ReadAllText(page), "(?:href|src)=\"~?/(?<path>[^\"?#@]+\\.(?:css|js|svg|png|woff2|json))(?=[\"?#])"))
                if (!File.Exists(Path.Combine(WebRoot, match.Groups["path"].Value)))
                    missing.Add($"{Path.GetFileName(Path.GetDirectoryName(page))}/{Path.GetFileName(page)} -> /{match.Groups["path"].Value}");

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// Keine Stylesheets in der Wurzel. Keine einzige.
    ///
    /// Sie ist der Ort, an dem etwas landet, wenn niemand entscheidet, wohin es gehört. Vier lagen
    /// noch dort und standen hier namentlich als Ausnahme; sie liegen jetzt unter styles/, wo die
    /// Schichten hingehören, und die Ausnahmeliste ist leer. Sie bleibt leer.
    /// </summary>
    [Fact]
    public void The_root_collects_no_stylesheets()
    {
        var found = Directory.EnumerateFiles(WebRoot, "*.css").Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).ToArray();

        Assert.True(found.Length == 0,
            "Stylesheets in der Wurzel. Sie gehören nach styles/, zu ihrer Seite oder zu ihrer Komponente:"
            + Environment.NewLine + string.Join(Environment.NewLine, found!));
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
    /// Jedes Stylesheet schließt, was es öffnet.
    ///
    /// Beim Verteilen von app.css auf die Seiten hat ein selbstgebauter Schnitt mehrzeilige
    /// Kommentare zerteilt: das /* blieb liegen, das */ zog mit um. Der Browser überliest dann alles
    /// bis zum nächsten */ — die Regeln stehen im Text und wirken nicht. Die ganze Suite blieb grün;
    /// gefunden hat es erst ein Vergleich der berechneten Stile im Browser.
    ///
    /// Das ist der billige Teil davon, und er hätte gereicht: ein offener Kommentar und eine
    /// unbalancierte Klammer sind im Text zu sehen, ohne Browser und in Millisekunden.
    /// </summary>
    [Fact]
    public void Every_stylesheet_closes_what_it_opens()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(WebRoot, "*.css", SearchOption.AllDirectories))
        {
            var css = File.ReadAllText(path);
            var opened = Regex.Matches(css, @"/\*").Count;
            var closed = Regex.Matches(css, @"\*/").Count;
            if (opened != closed)
            {
                offenders.Add($"{Relative(path)}: {opened}× /* aber {closed}× */");
                continue;
            }

            var code = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            var open = code.Count(character => character == '{');
            var close = code.Count(character => character == '}');
            if (open != close) offenders.Add($"{Relative(path)}: {open}× {{ aber {close}× }}");
        }

        Assert.True(offenders.Count == 0,
            "Diese Stylesheets sind nicht geschlossen. Alles danach fällt beim Parsen unter den Tisch:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Ein nachgebauter Klick trifft etwas.
    ///
    /// Zehn Stellen unter pages/networth/ riefen nach dem Speichern
    /// document.querySelector('#refresh')?.click() auf. Einen Knopf mit dieser Kennung gibt es in
    /// diesem Dokument nicht — er stand einmal in der eigenen Admin-Seite, die es nicht mehr gibt.
    /// Das Fragezeichen hat den Fehler verschluckt, also passierte schlicht nichts: kein Neuladen,
    /// keine Meldung, nur ein Wert, der auf dem Bildschirm alt blieb.
    ///
    /// Kein Test hat das gemeldet, weil der Quelltext richtig aussah. Dieser vergleicht die Kennung
    /// mit dem, was wirklich im Dokument steht.
    /// </summary>
    [Fact]
    public void A_synthetic_click_has_a_target()
    {
        var markup = WebSources.Layout();
        var offenders = new List<string>();

        foreach (var path in Scripts("app", "components", "core", "features", "pages"))
            foreach (Match match in Regex.Matches(Code(path), @"querySelector\('#([\w-]+)'\)\??\.click\(\)"))
                if (!markup.Contains($"id=\"{match.Groups[1].Value}\"", StringComparison.Ordinal))
                    offenders.Add($"{Relative(path)}: #{match.Groups[1].Value}");

        Assert.True(offenders.Count == 0,
            "Diese Module klicken auf etwas, das es im Dokument nicht gibt:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Eine Seite ist ein Ordner mit page.html, und index.html ist erzeugt, nicht gepflegt.
    /// </summary>
    [Fact]
    public void Every_page_folder_is_a_page()
    {
        var pages = Path.Combine(WebRoot, "pages");
        Assert.True(Directory.Exists(pages));

        var html = WebSources.Layout();
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

    /// <summary>
    /// Wer den Browser braucht, sagt es — und zwar so, wie CI danach fragt.
    ///
    /// Die Messungen laufen in einem eigenen Job, weil nur der den Browser installiert. Ausgewählt
    /// wurden sie einmal über den Klassennamen ("!~LayoutStability"), und die zweite Messklasse fiel
    /// deshalb in den falschen Job: zwölf Tests, alle rot, alle mit "Executable doesn't exist" — ein
    /// Fehler, der nichts über die Anwendung sagt und trotzdem den ganzen Lauf rot macht.
    ///
    /// Jetzt filtert CI nach <c>Needs=Browser</c>, und dieser Test hält die Markierung an den Klassen,
    /// die sie brauchen: alles, was sich die ui-harness teilt, teilt sich auch den Browser.
    /// </summary>
    [Fact]
    public void A_test_that_needs_the_browser_says_so()
    {
        var offenders = typeof(UiHarness).Assembly.GetTypes()
            .Where(type => type.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType.Name == "CollectionAttribute"
                && attribute.ConstructorArguments.Any(argument =>
                    (argument.Value as string) == nameof(UiHarnessCollection))))
            .Where(type => !type.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType.Name == "TraitAttribute"
                && attribute.ConstructorArguments.Select(argument => argument.Value as string)
                    .SequenceEqual(["Needs", "Browser"])))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Klassen teilen sich die ui-harness, tragen aber kein [Trait(\"Needs\", \"Browser\")] — "
            + "CI schickt sie damit in den Job ohne Browser:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", offenders));
    }
}
