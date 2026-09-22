using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #135, die Merkliste: sechs fertige Routen ohne einen einzigen Knopf.
///
/// Zwei Dinge sind hier gepinnt, und beide sind Fallen, die man erst beim Bauen sieht.
///
/// 1. <b>PUT ersetzt IMMER die ganze Liste.</b> <c>InvestmentStore.ReplaceWatchlistItemsAsync</c>
///    loescht zuerst alle Eintraege der Liste und schreibt dann, was ankam. Wer beim Aendern eines
///    Zielkurses nur diesen einen Eintrag schickt, loescht damit jedes andere beobachtete Papier -
///    und zwar still, denn die Anfrage ist erfolgreich.
/// 2. <b>Ein unbekannter Kurs ist keine Null.</b> Ein frisch notiertes Papier hat noch keinen Kurs;
///    die Zeile sagt das, statt 0,00 EUR zu zeigen.
/// </summary>
public sealed class WatchlistUiTests
{
    private static string PageJs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "FullWorth.Web", "wwwroot", "pages", "networth", "page.js"));
    }

    /// <summary>Die sechs Routen haben jetzt einen Aufrufer.</summary>
    [Fact]
    public void The_watchlist_endpoints_are_reachable_from_the_wealth_page()
    {
        var js = PageJs();

        Assert.Contains("api/investments/watchlists", js);
        Assert.Contains("/items", js);
        Assert.Contains("api/investments/securities", js);
    }

    /// <summary>
    /// Frontend-Regel 1: Liste UND Eintraege stehen vor dem ersten Zeichnen fest. Die Eintraege
    /// nachzuladen hiesse, die Karte waechst nachtraeglich.
    /// </summary>
    [Fact]
    public void Both_round_trips_happen_before_the_first_paint()
    {
        var js = PageJs();

        Assert.Contains("loadWatchlist()", js);
        // loadWatchlist steht IN dem grossen Promise.all der Seite, nicht daneben.
        var load = Regex.Match(js, @"await Promise\.all\(\[.*?\]\);", RegexOptions.Singleline);
        Assert.True(load.Success, "Der Ladeblock wurde nicht gefunden.");
        Assert.Contains("loadWatchlist()", load.Value);
    }

    /// <summary>
    /// Die Falle. Jeder Schreibweg geht ueber eine Funktion, die die VOLLSTAENDIGE Liste schickt -
    /// Hinzufuegen, Aendern und Entfernen bauen alle den neuen Gesamtstand und uebergeben ihn.
    /// </summary>
    [Fact]
    public void Every_write_sends_the_complete_list_because_the_server_replaces_it()
    {
        var js = PageJs();

        Assert.Contains("async function saveWatchlistItems(items)", js);
        Assert.Contains("items.map(item => ({", js);
        // Entfernen ist eine gefilterte Gesamtliste, Aendern eine abgebildete - nie ein Einzelstueck.
        Assert.Contains(".filter(other => other.securityId !== item.securityId)", js);
        Assert.Contains(".map(other => other.securityId === item.securityId", js);
    }

    /// <summary>Kein Kurs heisst kein Kurs - nicht null Euro.</summary>
    [Fact]
    public void A_security_without_a_price_says_so_instead_of_showing_zero()
    {
        var js = PageJs();

        Assert.Contains("item.price == null", js);
        Assert.Contains("watchlistNoPrice", js);
    }

    /// <summary>
    /// Wer etwas merken will, soll nicht erst eine Merkliste anlegen muessen. Gibt es noch keine,
    /// entsteht sie beim ersten Papier.
    /// </summary>
    [Fact]
    public void The_first_watched_security_creates_the_list_on_its_own()
    {
        var js = PageJs();

        Assert.Contains("if (!nw.watchlist?.id)", js);
        Assert.Contains("ctx.api('api/investments/watchlists', jsonBody({ name: t('watchlistTitle') }))", js);
    }

    /// <summary>Beide Sprachen - ein fehlender Text zeigt sonst den Schluessel in der Oberflaeche.</summary>
    [Fact]
    public void Both_languages_carry_the_watchlist_texts()
    {
        var js = PageJs();

        foreach (var key in new[] { "watchlistTitle", "watchlistEmpty", "watchlistAdd", "watchlistSecurity", "watchlistTarget", "watchlistNotes", "watchlistRemove", "watchlistNoPrice", "watchlistNoSecurities" })
            Assert.Equal(2, Regex.Matches(js, $@"\b{key}:").Count);
    }
}
