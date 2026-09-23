using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace FullWorth.Web.Navigation;

/// <summary>
/// Ueberschrift und Unterzeile einer Seite, serverseitig (#154).
///
/// Sie muessen im Dokument stehen, nicht nachgetragen werden. Die alte Huelle hatte deshalb die
/// Ueberschrift der Startseite fest im Markup - fuer jede Seite, und JavaScript tauschte sie danach
/// aus. Das reservierte immerhin den Platz.
///
/// Eine Razor-Seite kann es besser: sie weiss, welche Seite sie ist, und der Text steht in
/// <c>wwwroot/locales/de.json</c>, wo er ohnehin gepflegt wird. Ihn hier noch einmal einzutragen
/// waere eine zweite Liste.
///
/// Deutsch, weil das die Standardsprache der Oberflaeche ist. Eine englische Sitzung bekommt den
/// Text nach dem Laden ersetzt - das ist ein Textwechsel in derselben Zeile und kein Sprung. Eine
/// serverseitige Lokalisierung ist ausdruecklich nicht Teil dieses Architekturschnitts.
/// </summary>
public sealed class PageHeadings
{
    private readonly Dictionary<string, (string Title, string Subtitle)> headings = new(StringComparer.Ordinal);

    public PageHeadings(IWebHostEnvironment environment)
    {
        var file = Path.Combine(environment.WebRootPath ?? string.Empty, "locales", "de.json");
        if (!File.Exists(file)) return;

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        if (!document.RootElement.TryGetProperty("pages", out var pages)) return;

        foreach (var page in pages.EnumerateObject())
        {
            var title = page.Value.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (title is null) continue;
            var subtitle = page.Value.TryGetProperty("subtitle", out var s) ? s.GetString() : null;
            headings[page.Name] = (title, subtitle ?? string.Empty);
        }
    }

    public string Title(string? view) =>
        view is not null && headings.TryGetValue(view, out var heading) ? heading.Title : string.Empty;

    /// <summary>Eine Seite ohne Unterzeile hat keinen Schluessel dafuer - das ist Absicht, kein Fehler.</summary>
    public string Subtitle(string? view) =>
        view is not null && headings.TryGetValue(view, out var heading) ? heading.Subtitle : string.Empty;
}
