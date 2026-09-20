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
}
