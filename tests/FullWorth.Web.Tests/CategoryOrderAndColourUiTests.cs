using FullWorth.Web.Tests.Pwa;
namespace FullWorth.Web.Tests;

/// <summary>
/// Reihenfolge und Farbe der Kategorien (#177).
///
/// Drei Routen ohne Aufrufer, und zwei davon waren besonders leicht zu uebersehen, weil die Seite
/// ohne sie nicht kaputt aussah:
///
/// <list type="number">
///   <item><c>PUT /api/category-order</c>. Die Liste sortierte seit jeher nach <c>sortOrder</c>, der
///         Wert war also wirksam - nur konnte ihn niemand aendern. Die Reihenfolge, die jemand sah,
///         war die, die der Seeder einmal vergeben hatte.</item>
///   <item><c>GET/PUT .../category-appearances</c>. Der Punkt vor dem Namen bekam seine Farbe nach
///         ZAEHLERSTAND: dieselbe Kategorie war blau oder gruen, je nachdem, wie viele vor ihr
///         standen. Eine gespeicherte Farbe gab es, sie wurde nur nie gelesen.</item>
/// </list>
/// </summary>
public sealed class CategoryOrderAndColourUiTests
{
    private static string Seite() => WebSources.Asset("pages", "categories", "page.js");
    private static string Sortieren() => WebSources.Asset("pages", "categories", "arrange.js");

    [Fact]
    public void The_order_is_written_in_a_single_call()
    {
        var quelle = Sortieren();

        Assert.Contains("ctx.api('api/category-order', ctx.jsonBody({ items }, 'PUT'))", quelle, StringComparison.Ordinal);
        // Eine Anfrage je Zeile koennte auf halbem Weg stehenbleiben und eine Reihenfolge
        // hinterlassen, die niemand gewaehlt hat.
        Assert.Equal(1, quelle.Split("ctx.api(").Length - 1);
    }

    /// <summary>
    /// Nur was sich bewegt hat. Die ganze Liste zu schicken waere einfacher und wuerde jede Kategorie
    /// als geaendert protokollieren, auch die, die niemand angefasst hat.
    /// </summary>
    [Fact]
    public void Only_the_categories_that_moved_are_sent()
    {
        Assert.Contains("if ((node.sortOrder ?? 0) !== order)", Sortieren(), StringComparison.Ordinal);
        Assert.Contains("if (!items.length) return onDone(false);", Sortieren(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verschieben ist mit der Tastatur zu bedienen. Ein Ziehen waere hier ausserdem zweideutig - der
    /// Baum ist eingerueckt, und "unter die Kategorie darueber" ist etwas anderes als "eine Position
    /// hoeher". Das Umhaengen hat seinen eigenen, benannten Weg im Bearbeiten-Dialog.
    /// </summary>
    [Fact]
    public void Moving_works_without_a_mouse()
    {
        var quelle = Sortieren();

        Assert.Contains("setAttribute('aria-label'", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("pointerdown", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("dragstart", quelle, StringComparison.Ordinal);
        // Der Fokus geht mit: wer zweimal hintereinander verschiebt, soll den Knopf nicht neu suchen.
        Assert.Contains(".focus()", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Archivierte Kategorien bleiben aus der Ordnung heraus. Ihre Position mitzuschicken hiesse, sie
    /// beim Wiederherstellen an eine Stelle zu setzen, die niemand fuer sie gewaehlt hat.
    /// </summary>
    [Fact]
    public void Archived_categories_are_not_reordered()
    {
        Assert.Contains("rows.filter(row => !row.isArchived)", Sortieren(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_dot_carries_the_stored_colour_when_there_is_one()
    {
        var quelle = Seite();

        Assert.Contains("api/category-intelligence/category-appearances", quelle, StringComparison.Ordinal);
        Assert.Contains("appearance.has(String(node.id))", quelle, StringComparison.Ordinal);
        // Und ohne gespeicherte Farbe bleibt es beim bisherigen Zaehlerstand - kein Punkt ohne Farbe.
        Assert.Contains("data-cat=\"${catIndex}\"", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ein Farbfeld hat IMMER einen Wert. Ohne diesen Vergleich wuerde jedes Umbenennen einer
    /// Kategorie ihr eine Farbe geben, die niemand gewaehlt hat - und der Zaehlerstand-Fallback waere
    /// nach der ersten Bearbeitung fuer immer weg.
    /// </summary>
    [Fact]
    public void The_colour_is_only_written_when_it_changed()
    {
        Assert.Contains(
            "if (colour !== String(appearance.get(String(node.id)) || '').toUpperCase())",
            Seite(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_mode_is_reachable_and_precached()
    {
        Assert.Contains("data-action=\"arrange-categories\"", WebSources.Page("Categories"), StringComparison.Ordinal);
        Assert.Contains("data-action=\"arrange-categories\"", Seite(), StringComparison.Ordinal);
        PwaAssert.Ships("/pages/categories/arrange.js", WebSources.Asset("sw.js"));

        foreach (var sprache in new[] { "de", "en" })
        {
            var locale = WebSources.Asset("locales", $"{sprache}.json");
            Assert.Contains("\"arrange\"", locale, StringComparison.Ordinal);
            Assert.Contains("\"colour\"", locale, StringComparison.Ordinal);
        }
    }
}
