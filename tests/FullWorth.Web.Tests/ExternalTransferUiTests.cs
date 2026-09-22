namespace FullWorth.Web.Tests;

/// <summary>
/// #146, die zweite Haelfte: eine Umbuchung, deren Gegenkonto es in FullWorth nicht gibt.
///
/// Der Weg endete bisher im Leeren. Wer "Gegenbuchung waehlen" oeffnete und keine hatte - weil das
/// Geld auf ein Sparkonto ausser Haus ging -, sah eine leere Liste und konnte nichts tun. Die Buchung
/// blieb eine gewoehnliche Ausgabe und verfaelschte jede Auswertung.
///
/// Zwei Auswege, beide vom Nutzer bestaetigt. Was hier gepinnt ist, sind die zwei Verbote aus dem
/// Issue: es wird kein Konto ungefragt angelegt, und es wird keine Gegenbuchung erfunden.
/// </summary>
public sealed class ExternalTransferUiTests
{
    private static string PageJs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "FullWorth.Web", "wwwroot", "pages", "transactions", "page.js"));
    }

    /// <summary>Beide Moeglichkeiten stehen da, wo die leere Liste stand.</summary>
    [Fact]
    public void Both_ways_out_exist_where_the_empty_list_used_to_be()
    {
        var js = PageJs();

        Assert.Contains("data-external", js);
        Assert.Contains("data-new-account", js);
        Assert.Contains("api/transactions/${t.id}/transfer-external", js);
    }

    /// <summary>
    /// "Kein Konto ungefragt anlegen": der Name wird aus der Gegenpartei VORGESCHLAGEN und in einem
    /// Formular gezeigt, das der Nutzer abschickt. Ein direkter POST beim Klick waere genau das, was
    /// das Issue verbietet.
    /// </summary>
    [Fact]
    public void The_manual_account_is_proposed_and_confirmed_never_created_on_a_click()
    {
        var js = PageJs();

        Assert.Contains("function openManualCounterAccount(t)", js);
        Assert.Contains("values: { name: t.counterparty || '', balance: '' }", js);
        // Angelegt wird erst beim Abschicken des Formulars.
        Assert.Contains("onSubmit: async ({ values, close })", js);
    }

    /// <summary>
    /// "Keine erfundene Gegenbuchung": nach dem Anlegen des Kontos wird NICHT verknuepft. Das neue
    /// Konto hat noch keine Buchung - es gaebe nichts zu verknuepfen, und was man dafuer anlegen
    /// muesste, waere erfunden.
    /// </summary>
    [Fact]
    public void Creating_the_account_does_not_invent_a_counter_booking()
    {
        var js = PageJs();
        var start = js.IndexOf("function openManualCounterAccount(t)", StringComparison.Ordinal);
        Assert.True(start > 0, "openManualCounterAccount wurde nicht gefunden.");
        var body = js[start..];
        var end = body.IndexOf("\n}", StringComparison.Ordinal);
        body = end > 0 ? body[..end] : body;

        Assert.DoesNotContain("transfer-link", body);
        Assert.DoesNotContain("api/transactions'", body);
    }
}
