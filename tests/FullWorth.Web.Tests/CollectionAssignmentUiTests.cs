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

    /// <summary>Der Body einer benannten Funktion, von der oeffnenden bis zur passenden Klammer.</summary>
    private static string BodyOf(string js, string pattern, string name)
    {
        var match = Regex.Match(js, pattern, RegexOptions.Singleline);
        Assert.True(match.Success, $"{name} wurde nicht gefunden.");

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

    private static string OpenDetailBody() =>
        BodyOf(PageJs(), @"async function openDetail\([^)]*\)\s*\{", "openDetail(...)");

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

    /// <summary>
    /// #124 verlangt neben der Zuordnung im Detail auch die Massenzuordnung aus der Liste. Die
    /// Mehrfachauswahl gab es dort schon, sie fuehrte aber nur zum Coach - eine Reise oder Renovierung
    /// war damit Zeile fuer Zeile zuzuordnen, obwohl der Endpunkt eine ganze Liste auf einmal nimmt.
    /// </summary>
    [Fact]
    public void The_transaction_list_can_assign_a_whole_selection_at_once()
    {
        var js = PageJs();

        Assert.Contains("data-selection-collect", js);
        Assert.Contains("openBulkCollectionPicker(coachSelection.getSelectedIds())", js);

        var body = BodyOf(js, @"async function openBulkCollectionPicker\([^)]*\)\s*\{",
            "openBulkCollectionPicker(...)");
        // Eine Anweisung je Sammlung mit der ganzen Liste - nicht eine Runde je Buchung.
        Assert.Contains("api/collections/${collectionId}/transactions${suffix}", body);
        Assert.Contains("ctx.jsonBody({ transactionIds })", body);
        Assert.Contains("for (const collectionId of chosen)", body);
    }

    /// <summary>
    /// #124 verlangt beide Richtungen. Sie stehen im selben Dialog, statt die Auswahlleiste auf vier
    /// Knoepfe zu bringen: gewaehlt wird dieselbe Menge Sammlungen, nur die Richtung unterscheidet
    /// sich - und ein Dialog, der beides kann, braucht den Weg nicht doppelt.
    /// </summary>
    [Fact]
    public void Both_directions_share_one_dialog_and_one_write_path()
    {
        var body = BodyOf(PageJs(), @"async function openBulkCollectionPicker\([^)]*\)\s*\{",
            "openBulkCollectionPicker(...)");

        Assert.Contains("apply('', addButton)", body);
        Assert.Contains("apply('/remove', removeButton)", body);
        // Ein gemeinsamer Schreibweg, nicht zwei kopierte Schleifen.
        Assert.Single(Regex.Matches(body, @"for \(const collectionId of chosen\)"));
    }

    /// <summary>
    /// Aus der Liste wird HINZUGEFUEGT, nicht ersetzt. Dort sieht der Benutzer nicht, in welchen
    /// Sammlungen die markierten Buchungen schon stecken - und was man nicht sieht, darf man nicht
    /// ueberschreiben. Das Ersetzen gehoert ins Buchungsdetail, wo genau diese Liste sichtbar ist.
    /// </summary>
    [Fact]
    public void The_bulk_path_adds_and_never_replaces()
    {
        var body = BodyOf(PageJs(), @"async function openBulkCollectionPicker\([^)]*\)\s*\{",
            "openBulkCollectionPicker(...)");

        // Der Ersetzen-Endpunkt (PUT auf die Buchung) darf hier nicht auftauchen.
        Assert.DoesNotContain("/collections`", body);
        Assert.DoesNotContain("'PUT'", body);
    }

    /// <summary>
    /// #124 verlangt "direkt neue Sammlung anlegen können" im Buchungsdetail. Der Grund ist ein
    /// Umweg: wer beim Zuordnen merkt, dass die passende Sammlung fehlt, musste die Buchung
    /// schliessen, auf die Sammlungen-Seite gehen, anlegen und zurueckkommen - und wusste dann nicht
    /// mehr sicher, welche Buchung es war.
    /// </summary>
    [Fact]
    public void A_collection_can_be_created_without_leaving_the_transaction()
    {
        var js = PageJs();

        Assert.Contains("function createCollectionByName()", js);
        Assert.Contains("ctx.api('api/collections', ctx.jsonBody({ name:", js);
        // In beiden Auswahldialogen - im Detail und in der Massenzuordnung.
        Assert.Equal(2, Regex.Matches(js, @"\[data-new\]'\)\.onclick").Count);
    }

    /// <summary>
    /// Der Dialog nutzt den gemeinsamen Formulardialog, nicht <c>window.prompt</c>. Ein natives
    /// Eingabefenster ist nicht uebersetzbar, nicht gestaltbar und zeigt bei einem vergebenen Namen
    /// keine Serverantwort - der geteilte Dialog bleibt offen und sagt, was schiefging.
    /// </summary>
    [Fact]
    public void Creating_a_collection_uses_the_shared_dialog_not_a_native_prompt()
    {
        var js = PageJs();
        var body = BodyOf(js, @"function createCollectionByName\(\)\s*\{", "createCollectionByName()");

        Assert.Contains("openFormDialog({", body);
        Assert.Contains("setFormError(", body);
        Assert.DoesNotContain("window.prompt", js);
        Assert.DoesNotContain("window.alert", js);
    }

    /// <summary>
    /// Nach dem Anlegen darf die bisherige Auswahl nicht verloren sein: sie steckt nur in der
    /// Auswahlliste, und der Dialog wird dafuer geschlossen und neu geoeffnet.
    /// </summary>
    [Fact]
    public void Creating_a_collection_keeps_what_was_already_selected()
    {
        var body = OpenDetailBody();

        Assert.Contains("const keep = list.getSelectedIds();", body);
        Assert.Contains("chosenCollections = [...keep, created]", body);
        // Und die Liste wird frisch geholt, damit die neue Sammlung mit Namen dasteht.
        Assert.Contains("collectionRows = (await ctx.api('api/collections'))", body);
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
            foreach (var key in new[] { "assignNone", "assignEmpty", "addToCollection", "addToCollectionHint", "addAction", "removeFromCollection" })
                Assert.Contains($"\"{key}\"", json);
        }
    }
}
