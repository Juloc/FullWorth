using System.Text.Json;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// Handy und Desktop zeigen dasselbe Menü.
///
/// Sie taten es lange nicht: Admin fehlte am Telefon, Händler und Protokoll fehlten am Desktop,
/// Insights auf beiden. Der Grund war nicht Nachlässigkeit, sondern die Bauweise — die Seitenleiste
/// stand von Hand im Markup, und das Handy-Blatt las seine Einträge zur Laufzeit aus genau dieser
/// Seitenleiste aus. Zwei Listen, die auseinanderlaufen mussten.
///
/// Jetzt gibt es eine: <c>wwwroot/app/menu.js</c>. Dieser Test hält fest, dass alles daraus entsteht.
/// </summary>
public sealed class MenuParityTests
{
    private static readonly string WebRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "FullWorth.Web", "wwwroot"));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(WebRoot, relative));

    /// <summary>
    /// Die Einträge in Reihenfolge, gelesen aus der Definition selbst. Eine Zeile je Eintrag, und der
    /// view-Name ist das erste Feld — das reicht, um das Markup dagegen zu halten, ohne JavaScript
    /// auszuführen.
    /// </summary>
    private static string[] DefinedEntries() =>
        Regex.Matches(Read("app/menu.js"), @"\{ view: '([a-z]+)'")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static string[] QuickEntries() =>
        Regex.Match(Read("app/menu.js"), @"export const QUICK = \[([^\]]+)\]").Groups[1].Value
            .Split(',').Select(part => part.Trim().Trim('\'')).ToArray();

    private static string Section(string html, string id)
    {
        var match = Regex.Match(html, $"<!-- {id}:generiert -->(.*?)<!-- /{id} -->", RegexOptions.Singleline);
        Assert.True(match.Success, $"index.html hat keine erzeugte {id}-Sektion mehr.");
        return match.Groups[1].Value;
    }

    private static string[] EntriesIn(string markup) =>
        Regex.Matches(markup, @"data-entry=""([a-z]+)""").Select(match => match.Groups[1].Value).ToArray();

    /// <summary>
    /// Der eigentliche Punkt: die Seitenleiste ist die Definition, vollständig und in ihrer Reihenfolge.
    /// </summary>
    [Fact]
    public void The_sidebar_is_the_definition()
        => Assert.Equal(DefinedEntries(), EntriesIn(Section(Read("index.html"), "nav")));

    /// <summary>
    /// Die untere Leiste zeigt eine Auswahl — aber keine eigene. Jedes ihrer Ziele ist ein Eintrag der
    /// Definition, und „Mehr" öffnet denselben Baum, den die Seitenleiste zeigt (app.js baut ihn aus
    /// MENU, nicht mehr aus dem Markup der Seitenleiste).
    /// </summary>
    [Fact]
    public void The_phone_bar_shows_defined_entries_only()
    {
        var bottom = EntriesIn(Section(Read("index.html"), "bottom-nav"));

        Assert.Equal(QuickEntries(), bottom);
        Assert.All(bottom, entry => Assert.Contains(entry, DefinedEntries()));
    }

    /// <summary>
    /// Kein Eintrag darf nur am Telefon oder nur am Desktop erreichbar sein. Am Desktop steht alles in
    /// der Leiste; am Telefon steht alles, was nicht unten steht, hinter „Mehr". Beides zusammen muss
    /// die Definition ergeben — sonst ist genau das zurück, was dieser Test verhindern soll.
    /// </summary>
    [Fact]
    public void Nothing_is_reachable_on_only_one_of_the_two()
    {
        var appJs = Read("app.js");

        Assert.Contains("const MORE=ENTRIES.filter(entry=>!QUICK.includes(entry.view));", appJs);
        Assert.Contains("MENU.map(group=>", appJs);
        // Das Handy-Blatt las früher die Seitenleiste aus. Diese Abfrage darf es nicht mehr geben.
        Assert.DoesNotContain(".sidebar button[data-view=", appJs);
    }

    /// <summary>
    /// Jeder Eintrag führt zu etwas: zu einer Ansicht, die im Dokument steht, oder zu einer eigenen
    /// Adresse. Ein Menüpunkt, der ins Leere zeigt, ist schlimmer als keiner.
    ///
    /// Coach stand hier einmal namentlich als Ausnahme: es baute seine Ansicht selbst und war
    /// deshalb nicht im Dokument zu finden. Seit es eine gewöhnliche Seite ist, gibt es keine
    /// Ausnahme mehr — und dass es keine gibt, ist genau das, was dieser Test jetzt festhält.
    ///
    /// Seit #154 gibt es einen dritten Ort: eine Razor-Seite unter Pages/. Eine Ansicht, die dorthin
    /// umgezogen ist, steht mit voller Absicht NICHT mehr im Dokument der alten Hülle — der Eintrag
    /// führt trotzdem irgendwohin, nämlich an eine echte Adresse.
    /// </summary>
    [Fact]
    public void Every_entry_leads_somewhere()
    {
        var html = Read("index.html");
        var sidebar = Section(html, "nav");

        foreach (var entry in DefinedEntries())
        {
            var inDocument = html.Contains($"id=\"view-{entry}\"");
            var ownPage = sidebar.Contains($"data-entry=\"{entry}\"") && !sidebar.Contains($"data-view=\"{entry}\"");

            Assert.True(inDocument || ownPage || HasRazorPage(entry),
                $"Der Eintrag {entry} zeigt weder auf eine Ansicht, noch auf eine eigene Seite, noch auf eine Razor-Seite.");
        }
    }

    /// <summary>Eine Razor-Seite, deren @@page-Adresse auf diese Ansicht zeigt (#154).</summary>
    private static bool HasRazorPage(string entry)
    {
        var pages = new DirectoryInfo(Path.GetFullPath(Path.Combine(WebRoot, "..", "Pages")));
        if (!pages.Exists) return false;
        var address = "\"/" + entry + "\"";
        return pages.EnumerateFiles("*.cshtml", SearchOption.AllDirectories)
            .Any(file => File.ReadAllText(file.FullName).Contains("@page " + address, StringComparison.Ordinal));
    }

    /// <summary>
    /// Die Übersetzung gehört dazu. Ein Eintrag ohne Schlüssel zeigt seinen eigenen Schlüsselnamen an,
    /// und das fällt in der Seitenleiste sofort auf — aber eben erst dem Benutzer.
    /// </summary>
    [Fact]
    public void Every_label_exists_in_both_languages()
    {
        var keys = Regex.Matches(Read("app/menu.js"), @"label: '([a-z.]+)'")
            .Select(match => match.Groups[1].Value).Distinct();

        foreach (var language in new[] { "de", "en" })
        {
            using var document = JsonDocument.Parse(Read($"locales/{language}.json"));
            foreach (var key in keys)
            {
                var node = document.RootElement;
                foreach (var part in key.Split('.'))
                {
                    Assert.True(node.TryGetProperty(part, out node), $"{language}.json kennt {key} nicht.");
                }
            }
        }
    }
}
