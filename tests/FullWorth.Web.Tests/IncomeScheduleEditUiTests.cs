using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #173: Einnahmen-Vertraege liessen sich nicht oeffnen und nicht bearbeiten.
///
/// Wieder dasselbe Muster wie #135 und #124: <c>POST</c>, <c>PUT</c> und <c>DELETE</c> auf
/// <c>/api/income-schedules</c> existierten seit immer, das Frontend rief nur <c>GET</c> und die
/// Erkennung. Ein automatisch erkanntes Gehalt war damit sichtbar, aber unkorrigierbar - und weil die
/// Erkennung Betrag und Rhythmus raet, ist genau das der Fall, in dem man korrigieren will.
///
/// Eine eigene Sonderseite gibt es bewusst nicht: derselbe Formulardialog wie fuer Vertraege. Was
/// Einnahmen NICHT teilen, ist die Vertragsliste - dort zieht jeder Verbraucher den Betrag ab.
/// </summary>
public sealed class IncomeScheduleEditUiTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string PageJs() => File.ReadAllText(Path.Combine(
        Root(), "src", "FullWorth.Web", "wwwroot", "pages", "contracts", "page.js"));

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

    private static string DialogBody() =>
        BodyOf(PageJs(), @"async function openIncomeDialog\([^)]*\)\s*\{", "openIncomeDialog(...)");

    [Fact]
    public void An_income_row_opens_a_detail_dialog_and_can_be_saved()
    {
        var js = PageJs();

        Assert.Contains("data-income-edit", js);
        Assert.Contains("openIncomeDialog(schedule)", js);
        Assert.Contains("api/income-schedules/${schedule.id}", DialogBody());
        Assert.Contains("'PUT'", DialogBody());
    }

    /// <summary>
    /// Die Zeile ist per Tastatur erreichbar. Ein <c>div role="button"</c> ohne Enter/Space ist eine
    /// Schaltflaeche, die nur die Maus kennt.
    /// </summary>
    [Fact]
    public void The_row_is_reachable_by_keyboard()
    {
        var js = PageJs();

        Assert.Contains("role=\"button\" tabindex=\"0\" data-income-edit", js);
        var binding = js[js.IndexOf("data-income-edit]')", StringComparison.Ordinal)..];
        Assert.Contains("'keydown'", binding[..600]);
        Assert.Contains("event.key === 'Enter' || event.key === ' '", binding[..600]);
    }

    /// <summary>
    /// Kein Betrag ist ein echter Fall, keine fehlende Eingabe: eine schwankende Einnahme hat keinen
    /// festen Wert, und 0,00 € zu speichern waere eine erfundene Zahl. Das Feld darf deshalb nicht
    /// required sein.
    /// </summary>
    [Fact]
    public void A_varying_income_may_be_saved_without_an_amount()
    {
        var body = DialogBody();
        var amountField = body[body.IndexOf("name: 'amount'", StringComparison.Ordinal)..];
        amountField = amountField[..amountField.IndexOf('}')];

        Assert.DoesNotContain("required: true", amountField);
        Assert.Contains("chosenVaries || values.amount === '' ? null : Number(values.amount)", body);
    }

    /// <summary>
    /// Ob ein Plan schwankt, wird nach der Regel des Servers bestimmt
    /// (<c>AnalyticsService</c>: <c>ValueMode == "average"</c> ODER kein Betrag), nicht nur ueber die
    /// Zeichenkette. Ein Plan ohne Betrag schwankt, egal was im Feld steht - sonst geht er als
    /// "fester Betrag" auf und wird beim Speichern auch so festgeschrieben.
    /// </summary>
    [Fact]
    public void Whether_an_income_varies_follows_the_servers_own_rule()
    {
        Assert.Contains("schedule.valueMode === 'average' || schedule.expectedAmount == null", DialogBody());
    }

    /// <summary>
    /// Gespeichert wird, was der Benutzer im Formular gewaehlt hat - nicht der Zustand beim Oeffnen.
    /// Beides zu verwechseln hiess: wer von "schwankt" auf einen festen Betrag wechselt und ihn
    /// eintippt, haette ihn beim Speichern verloren, weil der Oeffnungszustand ihn auf null setzt.
    /// </summary>
    [Fact]
    public void Saving_uses_the_chosen_mode_and_not_the_state_at_open_time()
    {
        var body = DialogBody();
        var submit = body[body.IndexOf("onSubmit:", StringComparison.Ordinal)..];

        Assert.Contains("const chosenVaries =", submit);
        // Im Speicherpfad darf der Oeffnungszustand nicht mehr auftauchen.
        Assert.DoesNotMatch(@"expectedAmount: varies\b", submit);
        Assert.DoesNotMatch(@"valueMode: varies \?", submit);
    }

    /// <summary>
    /// Der erkannte Gegenpartei-Schluessel wird durchgereicht, nicht neu erfunden: daran haengt die
    /// Zuordnung kuenftiger Eingaenge, und er ist nichts, was jemand in ein Formular tippt. Ihn zu
    /// verlieren hiesse, dass das naechste Gehalt nicht mehr erkannt wird.
    /// </summary>
    [Fact]
    public void The_detected_counterparty_key_survives_an_edit()
    {
        Assert.Contains("normalizedCounterparty: schedule.normalizedCounterparty ?? null", DialogBody());
    }

    /// <summary>
    /// Stilllegen, nicht loeschen: <c>DELETE</c> archiviert serverseitig
    /// (<c>ArchiveScheduleAsync</c>), damit ausgewertete Vergangenheit erhalten bleibt. Der Knopf und
    /// die Rueckfrage muessen das sagen, statt ein Loeschen zu versprechen, das nicht passiert.
    /// </summary>
    [Fact]
    public void Deactivating_is_named_for_what_it_does()
    {
        var js = PageJs();
        var body = BodyOf(js, @"async function removeIncome\([^)]*\)\s*\{", "removeIncome(...)");

        Assert.Contains("method: 'DELETE'", body);
        Assert.Contains("Stilllegen", body);
        Assert.Contains("Vergangene Auswertungen bleiben erhalten", body);
        // Und es fragt vorher.
        var confirmIndex = body.IndexOf("ctx.confirm(", StringComparison.Ordinal);
        var apiIndex = body.IndexOf("ctx.api(", StringComparison.Ordinal);
        Assert.True(confirmIndex >= 0 && apiIndex > confirmIndex);
    }

    /// <summary>
    /// Wiederverwendung: derselbe Formulardialog wie die Vertraege, keine zweite Dialogsprache nur
    /// fuer Einnahmen (#157/#159).
    /// </summary>
    [Fact]
    public void The_dialog_reuses_the_shared_form_dialog()
    {
        var body = DialogBody();

        Assert.Contains("openFormDialog({", body);
        Assert.Contains("FieldKind.Select", body);
        Assert.DoesNotContain("<form", body);
    }
}
