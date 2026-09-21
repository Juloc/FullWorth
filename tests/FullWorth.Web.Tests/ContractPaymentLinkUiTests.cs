using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #135, zwei der sechs Funktionen ohne Zugang: die Zahlungsverknuepfungen eines Vertrags und das
/// Kuendigungsschreiben.
///
/// Beide waren backendseitig fertig - <c>ContractLinkEndpoints</c> mit vier Routen,
/// <c>ContractCancellationEndpoints.CancellationLetter</c> mit dem gebauten Brief - und hatten
/// zusammen null Aufrufer im Frontend. Der Befund in #135 nennt genau das den teuersten Zustand:
/// gepflegter Code, der nie laeuft. Diese Datei haelt die Aufrufer fest, damit das nicht
/// zurueckfaellt.
///
/// Reine Quelltextpruefungen, wie <see cref="CollectionAssignmentUiTests"/>.
/// </summary>
public sealed class ContractPaymentLinkUiTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Asset(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root(), "src", "FullWorth.Web", "wwwroot", .. parts]));

    private static string PageJs() => Asset("pages", "contracts", "page.js");

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

    /// <summary>
    /// Die Verknuepfungen werden mit dem Vertragsdetail geladen, nicht erst beim Oeffnen des
    /// Zahlungsdialogs: der Dialog zeigt sie je Zeile an, und ein Nachladen an dieser Stelle waere ein
    /// sichtbarer Sprung in einer Liste, die schon steht (Frontend-Regel 1).
    /// </summary>
    [Fact]
    public void Contract_detail_loads_the_payment_links_with_everything_else()
    {
        var body = BodyOf(PageJs(), @"async function openDetail\([^)]*\)\s*\{", "openDetail(...)");

        Assert.Contains("api/contracts/${id}/links", body);
        var loadIndex = body.IndexOf("api/contracts/${id}/links", StringComparison.Ordinal);
        var dialogIndex = body.IndexOf("ctx.dialog(", StringComparison.Ordinal);
        Assert.True(loadIndex >= 0 && dialogIndex > loadIndex,
            "Die Verknuepfungen muessen vor dem Bauen des Markups geladen sein.");

        // Ein fehlender Verknuepfungsabruf darf das Vertragsdetail nicht mitnehmen.
        Assert.Matches(@"api/contracts/\$\{id\}/links`\)\.catch\(\(\) => \[\]\)", body);
        // Und sie muessen im Zahlungsdialog tatsaechlich ankommen - ohne das dritte Argument zeigt er
        // wieder die alte, herkunftslose Liste.
        Assert.Contains("openPaymentsDialog(contract, payments, links)", body);
    }

    /// <summary>
    /// Die Herkunft je Zahlung ist der Punkt der Anzeige: eine automatisch erkannte Zuordnung darf
    /// falsch sein, eine selbst gesetzte nicht. Alle drei Quellen, die das Backend kennt
    /// (<c>detection</c>, <c>import</c>, <c>manual</c>), muessen unterscheidbar sein.
    /// </summary>
    [Fact]
    public void Every_link_source_the_backend_can_return_has_its_own_label()
    {
        var js = PageJs();
        var body = BodyOf(js, @"function linkSourceLabel\([^)]*\)\s*\{", "linkSourceLabel(...)");

        Assert.Contains("'detection'", body);
        Assert.Contains("'import'", body);
        // manual ist der Rest-Fall, deshalb kein eigener Vergleich - aber ein eigener Text.
        Assert.Equal(3, Regex.Matches(body, @"return t\(").Count);
    }

    /// <summary>
    /// Loesen ist die eine Schreiboperation dieses Dialogs und nutzt die DELETE-Route. Sie loescht die
    /// Zuordnung, nicht die Buchung - deshalb eine Rueckfrage, aber ausdruecklich keine als
    /// zerstoerend markierte: die Buchung bleibt, wo sie ist.
    /// </summary>
    [Fact]
    public void Unlinking_uses_the_delete_route_and_asks_first_without_claiming_to_destroy_anything()
    {
        var body = BodyOf(PageJs(), @"function openPaymentsDialog\([^)]*\)\s*\{", "openPaymentsDialog(...)");

        Assert.Contains("api/contracts/${contract.id}/links/${button.dataset.unlink}", body);
        Assert.Contains("method: 'DELETE'", body);

        var confirmIndex = body.IndexOf("ctx.confirm(", StringComparison.Ordinal);
        var apiIndex = body.IndexOf("ctx.api(`api/contracts/", StringComparison.Ordinal);
        Assert.True(confirmIndex >= 0 && apiIndex > confirmIndex, "Erst fragen, dann loeschen.");
        Assert.DoesNotContain("destructive: true", body);
    }

    /// <summary>
    /// Der Brief kommt fertig vom Server. Ein zweiter Textbau im Frontend waere die Dopplung, die
    /// spaeter auseinanderlaeuft - der Dialog darf ihn deshalb anzeigen, aber nicht zusammensetzen.
    /// </summary>
    [Fact]
    public void The_cancellation_letter_is_shown_not_rebuilt_in_the_frontend()
    {
        var body = BodyOf(PageJs(), @"async function openCancellationLetter\([^)]*\)\s*\{", "openCancellationLetter(...)");

        Assert.Contains("api/contracts/${contract.id}/cancellation-letter", body);
        // Kein Satzbaustein des Briefs im Frontend.
        Assert.DoesNotContain("Hiermit kündige", body);
        Assert.DoesNotContain("Kunden-/Vertragsnummer", body);
    }

    /// <summary>
    /// Der Endpunkt liefert <c>text/plain</c>. <c>ctx.api</c> parst jede Antwort als JSON und wuerde
    /// daran scheitern, deshalb gibt es <c>ctx.apiText</c> - denselben Client, nur ohne
    /// <c>JSON.parse</c>. Genau ein zusaetzlicher Abrufweg, kein zweiter Client.
    /// </summary>
    [Fact]
    public void A_plain_text_endpoint_is_read_through_the_shared_client_not_a_second_fetch()
    {
        var js = PageJs();
        Assert.Contains("ctx.apiText(`api/contracts/${contract.id}/cancellation-letter`)", js);
        // Keine eigene fetch-Runde an der Api vorbei.
        Assert.DoesNotContain("fetch(", js);

        var appJs = Asset("app.js");
        Assert.Contains("apiText:path=>apiClient.backendResponse(path).then(response=>response.text())", appJs);
    }

    /// <summary>
    /// Der Brieftext wird gesetzt, nicht ins Markup interpoliert: er enthaelt die vom Nutzer erfasste
    /// Kundennummer, und die hat in einer Vorlagenzeichenkette nichts verloren.
    /// </summary>
    [Fact]
    public void The_letter_text_is_assigned_never_interpolated_into_markup()
    {
        var body = BodyOf(PageJs(), @"async function openCancellationLetter\([^)]*\)\s*\{", "openCancellationLetter(...)");

        Assert.Contains("[data-letter]').value = letter", body);
        // Weder roh noch escaped im Template - der Wert gehoert nicht ins Markup.
        Assert.DoesNotContain("${letter}", body);
        Assert.DoesNotContain("ctx.esc(letter)", body);
    }

    [Fact]
    public void Both_locales_carry_the_cancellation_letter_label()
    {
        foreach (var locale in new[] { "de", "en" })
            Assert.Contains("\"cancellationLetter\"", Asset("locales", $"{locale}.json"));
    }

    /// <summary>
    /// #135, die Gegenrichtung zum Loesen: eine Zahlung von Hand zuordnen. Die Erkennung findet das
    /// Meiste, aber nicht alles - eine Zahlung ueber ein anderes Konto oder mit abweichendem
    /// Verwendungszweck bleibt liegen, und ohne diesen Weg liess sie sich gar nicht nachtragen.
    /// </summary>
    [Fact]
    public void A_payment_can_be_linked_by_hand()
    {
        var js = PageJs();

        Assert.Contains("data-link-payment", js);
        var body = BodyOf(js, @"async function openPaymentLinkPicker\([^)]*\)\s*\{", "openPaymentLinkPicker(...)");
        Assert.Contains("api/contracts/${contract.id}/links", body);
        Assert.Contains("linkSource: 'manual'", body);
    }

    /// <summary>
    /// Der Server lehnt alles ab, was keine Ausgabe ist. Die Auswahl zeigt deshalb von vornherein nur
    /// welche - eine Zeile, die beim Klick scheitert, ist schlimmer als eine, die gar nicht dasteht.
    /// Doppelt gesichert, weil der Filtername schon einmal falsch war ("out" statt "expense").
    /// </summary>
    [Fact]
    public void Only_expenses_are_offered_because_the_server_rejects_the_rest()
    {
        var body = BodyOf(PageJs(), @"async function openPaymentLinkPicker\([^)]*\)\s*\{", "openPaymentLinkPicker(...)");

        Assert.Contains("direction: 'expense'", body);
        Assert.Contains("Number(item.amount) < 0", body);
        // Der Betrag geht positiv hin - der Endpunkt verlangt Amount > 0.
        Assert.Contains("Math.abs(Number(item.amount))", body);
    }

    /// <summary>
    /// #135: "Vertrag aufteilen". <c>POST /api/contracts/{id}/split</c> war fertig und ohne Aufrufer.
    /// </summary>
    [Fact]
    public void Splitting_a_contract_is_reachable_from_its_detail()
    {
        var js = PageJs();

        Assert.Contains("data-split", js);
        Assert.Contains("openSplitDialog(contract)", js);
        Assert.Contains("api/contracts/${contract.id}/split", BodyOf(js,
            @"async function openSplitDialog\([^)]*\)\s*\{", "openSplitDialog(...)"));
    }

    /// <summary>
    /// Der Server verlangt mindestens zwei Teile, deren Summe dem Vertragsbetrag entspricht, und
    /// lehnt alles andere ab. Der Dialog rechnet deshalb mit und sperrt das Absenden, bis es passt -
    /// eine Ablehnung nach dem Absenden waere die schlechtere Haelfte derselben Regel.
    /// </summary>
    [Fact]
    public void The_split_dialog_refuses_a_sum_the_server_would_reject()
    {
        var body = BodyOf(PageJs(), @"async function openSplitDialog\([^)]*\)\s*\{", "openSplitDialog(...)");

        // Dieselbe Toleranz wie der Server (0.01).
        Assert.Contains("Math.abs(difference) <= 0.01", body);
        Assert.Contains("submit.disabled = !(enough && matches)", body);
        // Mindestens zwei gefuellte Zeilen.
        Assert.Contains("row.amount > 0).length >= 2", body);
        // Und die letzten zwei Zeilen lassen sich nicht wegloeschen.
        Assert.Contains("length <= 2) return;", body);
    }

    /// <summary>
    /// #135: die Fristen-Uebersicht. Die Frist eines einzelnen Vertrags stand schon in seiner Zeile;
    /// <c>GET /api/contracts/cancellation-deadlines</c> - die Frage "muss ich diese Woche etwas tun" -
    /// hatte keinen Aufrufer.
    /// </summary>
    [Fact]
    public void The_upcoming_deadlines_overview_is_wired()
    {
        var js = PageJs();

        Assert.Contains("api/contracts/cancellation-deadlines", js);
        var body = BodyOf(js, @"async function loadDeadlines\([^)]*\)\s*\{", "loadDeadlines(...)");
        // Ueberfaelliges gehoert nicht in eine Vorschau, und alles zu zeigen beantwortet die Frage nicht.
        Assert.Contains("Number(row.days) >= 0", body);
        Assert.Contains("<= 92", body);
        // Die Tage rechnet der Server; hier wird nichts nachgerechnet.
        Assert.DoesNotContain("Date.now()", body);
    }

    /// <summary>
    /// Das Feld steht UEBER der Liste. Es nachtraeglich einzublenden hat dieselbe Liste schon einmal
    /// um 355 Pixel geschoben (der Kommentar in renderContracts haelt das fest), deshalb muss es im
    /// gemeinsamen Warteblock laden - nicht danach.
    /// </summary>
    [Fact]
    public void The_deadlines_panel_loads_with_the_other_hint_panels_not_after_them()
    {
        var js = PageJs();
        // Am Hinweisfeld-Block verankert, nicht am ersten Promise.all der Datei - das laedt
        // Kategorien und Konten und hat mit dieser Zusage nichts zu tun.
        var index = js.IndexOf("loadDetected(false, staged)", StringComparison.Ordinal);
        Assert.True(index >= 0, "Der gemeinsame Warteblock der Hinweisfelder wurde nicht gefunden.");
        var start = js.LastIndexOf("await Promise.all([", index, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var block = js[start..js.IndexOf("]);", index, StringComparison.Ordinal)];

        Assert.Contains("loadDeadlines(staged)", block);
        Assert.Contains("loadIncome(false, staged)", block);
    }
}
