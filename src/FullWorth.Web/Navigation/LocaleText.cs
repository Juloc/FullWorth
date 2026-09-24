using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace FullWorth.Web.Navigation;

/// <summary>
/// Die deutschen Texte aus <c>wwwroot/locales/de.json</c>, serverseitig gelesen (#154).
///
/// Wofuer: Markup, das einen <c>data-i18n</c>-Schluessel traegt, muss den fertigen Text schon
/// enthalten. Sonst steht beim ersten Zeichnen ein leeres oder falsches Wort da, und JavaScript
/// ersetzt es danach - das ist ein Sprung, und zwar einer, der in der alten Huelle unsichtbar war:
/// dort wurde ALLES beim Start uebersetzt, waehrend noch keine Ansicht zu sehen war. Auf einer
/// echten Seite passiert es vor den Augen des Benutzers.
///
/// Dieselbe Regel befolgt <c>ops/generate-shell.mjs</c> seit jeher fuer das Menue der alten Huelle -
/// hier ist sie fuer Razor.
///
/// Welche Sprache das ist, entscheidet <see cref="Language"/> - und zwar nach genau derselben Regel
/// wie <c>core/state.js</c> im Browser. Weichen die beiden ab, tauscht JavaScript den Text nach dem
/// ersten Bild doch wieder aus, und der Sprung ist zurueck.
/// </summary>
public sealed class LocaleText
{
    /// <summary>Das Cookie, das <c>app/boot.js</c> vor dem ersten Zeichnen setzt.</summary>
    public const string CookieName = "fw.lang";

    private readonly Dictionary<string, JsonDocument> documents = new(StringComparer.Ordinal);

    public LocaleText(IWebHostEnvironment environment)
    {
        foreach (var language in new[] { "de", "en" })
        {
            var file = Path.Combine(environment.WebRootPath ?? string.Empty, "locales", $"{language}.json");
            if (File.Exists(file)) documents[language] = JsonDocument.Parse(File.ReadAllText(file));
        }
    }

    /// <summary>
    /// Dieselbe Regel wie <c>core/state.js</c>: erst die gespeicherte Wahl, sonst die Sprache des
    /// Browsers, sonst Deutsch.
    ///
    /// Das Cookie traegt die gespeicherte Wahl - aber erst ab dem ZWEITEN Aufruf, denn gesetzt wird
    /// es von <c>app/boot.js</c>, also im Browser. Beim allerersten Besuch gibt es keines, und dann
    /// ist <c>Accept-Language</c> die einzige Auskunft darueber, was JavaScript gleich anzeigen wird.
    /// Ohne diesen zweiten Zweig bekaeme jeder englische Besucher sein erstes Bild auf Deutsch und
    /// saehe es danach umspringen - einmal pro Browser, aber die Regel kennt kein "nur einmal".
    ///
    /// <c>navigator.language</c> ist das erste Sprachkuerzel aus eben diesem Kopf, deshalb genuegt
    /// das erste; die Gewichte dahinter sind fuer diese Entscheidung ohne Belang.
    /// </summary>
    public static string Language(HttpContext? context)
    {
        if (context is null) return "de";

        if (context.Request.Cookies.TryGetValue(CookieName, out var stored)
            && (string.Equals(stored, "en", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stored, "de", StringComparison.OrdinalIgnoreCase)))
            return stored.ToLowerInvariant();

        var preferred = context.Request.Headers.AcceptLanguage.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()?
            .Split(';')[0]
            .Trim();

        if (string.IsNullOrEmpty(preferred)) return "de";
        return preferred.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
    }

    /// <summary>Ein Schluessel wie <c>nav.start</c>. Unbekannt heisst leer, nicht der Schluesselname.</summary>
    public string Get(string? key, string language = "de")
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        if (!documents.TryGetValue(language, out var document)
            && !documents.TryGetValue("de", out document)) return string.Empty;

        var current = document.RootElement;
        foreach (var part in key.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return string.Empty;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : string.Empty;
    }
}
