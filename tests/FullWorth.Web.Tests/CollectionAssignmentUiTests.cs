using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #124, die zweite Zuordnungsachse im Buchungsdetail.
///
/// Diese Datei existiert wegen eines Befunds, der ohne sie wiederkommt: die beiden Endpunkte
/// <c>GET</c>/<c>PUT /api/transactions/{id}/collections</c> waren seit der Einfuehrung der Sammlungen
/// fertig - inklusive eines Kommentars in <c>CollectionEndpoints.cs</c>, der sie woertlich "das
/// Multi-Select im Buchungsdetail" nennt - und hatten trotzdem monatelang keinen einzigen Aufrufer im
/// Frontend. Backend-Tests waren gruen, das Feature war unerreichbar. Genau dieselbe Form wie die
/// sechs Funktionen in #135.
///
/// Reine Quelltextpruefungen, wie <see cref="TransactionForecastTests"/>: was hier gepinnt wird -
/// dass der Aufrufer ueberhaupt existiert, dass er vor dem ersten Zeichnen laedt und dass er nicht bei
/// jedem Speichern schreibt - steht im Text der Datei, unabhaengig davon, was eine Anfrage zurueckgibt.
/// </summary>
public sealed class CollectionAssignmentUiTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string PageJs() => File.ReadAllText(Path.Combine(
        Root(), "src", "FullWorth.Web", "wwwroot", "pages", "transactions", "page.js"));

    /// <summary>Der Body von <c>openDetail(...)</c>, von der oeffnenden bis zur passenden Klammer.</summary>
    private static string OpenDetailBody()
    {
        var js = PageJs();
        var match = Regex.Match(js, @"async function openDetail\([^)]*\)\s*\{", RegexOptions.Singleline);
        Assert.True(match.Success, "openDetail(...) wurde nicht gefunden.");

        var start = match.Index + match.Length;
        var depth = 1;
        var end = start;
        while (depth > 0 && end < js.Length)
        {
            if (js[end] == '{') depth++;
            else if (js[end] == '}') depth--;
            end++;
        }
        return js[start..end];
    }

    /// <summary>
    /// Der Kern: beide Richtungen werden tatsaechlich aufgerufen. Lesen allein wuerde die Zuordnung nur
    /// anzeigen; schreiben allein gaebe es nichts anzuzeigen.
    /// </summary>
    [Fact]
    public void Transaction_detail_reads_and_writes_the_collections_of_a_transaction()
    {
        var body = OpenDetailBody();

        Assert.Contains("api/transactions/${listItem.id}/collections", body);
        Assert.Contains("api/transactions/${t.id}/collections", body);
        // Die Liste der waehlbaren Sammlungen kommt aus derselben Quelle wie die Sammlungen-Seite,
        // nicht aus einer zweiten, eigens gebauten Abfrage.
        Assert.Contains("ctx.api('api/collections')", body);
    }

    /// <summary>
    /// Frontend-Regel 1: eine Zeile, die erst nach dem ersten Zeichnen erscheint, verschiebt das bereits
    /// gezeichnete Formular. Die Sammlungen muessen deshalb im selben Ladeblock stehen wie Detail und
    /// Kategorien - vor dem <c>ctx.dialog(...)</c>, das das Markup baut. Genau diese Reihenfolge hat
    /// beim Nachladen der Prognose (#139) schon einmal ein CLS-Budget gerissen.
    /// </summary>
    [Fact]
    public void Collections_are_loaded_before_the_drawer_markup_is_built_not_afterwards()
    {
        var body = OpenDetailBody();

        var readIndex = body.IndexOf("api/transactions/${listItem.id}/collections", StringComparison.Ordinal);
        var dialogIndex = body.IndexOf("ctx.dialog(", StringComparison.Ordinal);
        Assert.True(readIndex >= 0 && dialogIndex >= 0);
        Assert.True(readIndex < dialogIndex,
            "Die Sammlungen muessen geladen sein, bevor das Dialog-Markup gebaut wird - sonst waechst die Zeile nachtraeglich.");
    }

    /// <summary>
    /// Ein fehlgeschlagener Abruf darf nur diese eine Zeile kosten, nicht das ganze Buchungsdetail. Ohne
    /// eigenes <c>catch</c> landet ein 403 (fehlende Berechtigung) im gemeinsamen <c>catch</c>, und der
    /// Nutzer kann seine Buchung gar nicht mehr oeffnen - eine Zuordnungsachse, die die Hauptfunktion
    /// mitnimmt.
    /// </summary>
    [Fact]
    public void A_failing_collections_request_does_not_block_the_whole_detail_drawer()
    {
        var body = OpenDetailBody();

        Assert.Matches(@"\]\)\.catch\(\(\)\s*=>\s*null\)", body);
        // Und die Zeile selbst entfaellt dann, statt mit leeren Werten dazustehen.
        Assert.Contains("const collectionsRow = collectionRows", body);
    }

    /// <summary>
    /// Die Zuordnung wird nur geschrieben, wenn sie sich geaendert hat. Ohne diese Wache wuerde jedes
    /// blosse Speichern einer Kategorie oder Notiz die Sammlungen neu schreiben, obwohl der Nutzer den
    /// Auswahldialog nie geoeffnet hat - ein Schreibvorgang ohne Absicht, der bei gleichzeitiger
    /// Bearbeitung an anderer Stelle fremde Aenderungen ueberschreibt.
    /// </summary>
    [Fact]
    public void Collections_are_only_written_when_the_selection_actually_changed()
    {
        var body = OpenDetailBody();
        var js = PageJs();

        Assert.Contains("if (collectionRows && !sameIdSet(chosenCollections, assignedCollections))", body);
        // Reihenfolgeunabhaengig: die Auswahlliste liefert in Zeilenreihenfolge, der Server in seiner
        // eigenen. Ein Vergleich per join() waere hier still falsch.
        Assert.Contains("function sameIdSet(a, b)", js);
        Assert.Contains("new Set(a)", js);
    }

    /// <summary>
    /// Die Auswahl wird erst beim Uebernehmen des Detaildialogs geschrieben, nicht beim Schliessen des
    /// Auswahldialogs - sonst haette "Abbrechen" im Detail fuer die Sammlungen eine andere Bedeutung als
    /// fuer Kategorie, Notiz und die Schalter daneben.
    /// </summary>
    [Fact]
    public void Picking_collections_does_not_save_on_its_own()
    {
        var body = OpenDetailBody();

        var applyIndex = body.IndexOf("picker.querySelector('[data-apply]')", StringComparison.Ordinal);
        Assert.True(applyIndex >= 0, "Der Uebernehmen-Knopf des Auswahldialogs wurde nicht gefunden.");

        // Vom Uebernehmen-Knopf bis zum Ende seines Handlers darf kein Serveraufruf stehen.
        var close = body.IndexOf("picker.showModal()", StringComparison.Ordinal);
        Assert.True(close > applyIndex);
        var handler = body[applyIndex..close];
        Assert.DoesNotContain("ctx.api(", handler);
    }

    /// <summary>
    /// Wiederverwendung statt Nachbau (#160): die Mehrfachauswahl ist die gemeinsame Auswahlliste, nicht
    /// eine achte handgebaute Checkbox-Liste. Die Knoepfe tragen die gemeinsamen Rollen (#158).
    /// </summary>
    [Fact]
    public void The_picker_reuses_the_shared_selection_list_and_button_roles()
    {
        var body = OpenDetailBody();

        Assert.Contains("createSelectionList()", body);
        Assert.Contains("selectionListHtml(items", body);
        Assert.Contains("buttonClass(ButtonRole.Primary)", body);
        Assert.Contains("buttonClass(ButtonRole.Secondary)", body);
        // Keine handgeschriebene Checkbox-Zeile daneben.
        var pickerStart = body.IndexOf("createSelectionList()", StringComparison.Ordinal);
        var pickerEnd = body.IndexOf("picker.showModal()", StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"checkbox\"", body[pickerStart..pickerEnd]);
    }

    /// <summary>Die beiden neuen Texte stehen in beiden Sprachdateien - eine fehlende Uebersetzung zeigt
    /// sonst den rohen Schluessel im Dialog.</summary>
    [Fact]
    public void Both_locales_carry_the_new_collection_assignment_texts()
    {
        foreach (var locale in new[] { "de", "en" })
        {
            var json = File.ReadAllText(Path.Combine(
                Root(), "src", "FullWorth.Web", "wwwroot", "locales", $"{locale}.json"));
            Assert.Contains("\"assignNone\"", json);
            Assert.Contains("\"assignEmpty\"", json);
        }
    }
}
