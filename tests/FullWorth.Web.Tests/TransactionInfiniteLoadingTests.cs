namespace FullWorth.Web.Tests;

/// <summary>
/// #161, Teil C: unsichtbares Nachladen der Buchungsliste.
///
/// Quelltextpruefungen wie in <see cref="TransactionForecastTests"/> - ohne Datenbank und ohne HTTP,
/// weil das, was hier festgehalten wird, am Text der Datei wahr ist und nicht an einer einzelnen
/// Antwort: dass die Liste per Cursor blaettert statt einen Block zu holen, dass sie dabei immer bei
/// den JUENGSTEN Buchungen anfaengt, dass sie beim Nachladen nicht springt und dass der Ankerpunkt
/// mit dem Rand der Liste wandert.
/// </summary>
public sealed class TransactionInfiniteLoadingTests
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

    private static string PageCss() => File.ReadAllText(Path.Combine(
        Root(), "src", "FullWorth.Web", "wwwroot", "pages", "transactions", "page.css"));

    /// <summary>
    /// Die Seite holt keinen festen Block mehr. 500 Zeilen waren beides zugleich: eine grosse Abfrage
    /// UND eine stille Obergrenze - was danach kam, gab es fuer die Seite nicht. Genau daran haengt
    /// der eigentliche Fehler, den #161 beseitigt: aufsteigend waren diese 500 die aeltesten.
    /// </summary>
    [Fact]
    public void The_list_no_longer_asks_for_one_fixed_block()
    {
        var js = PageJs();

        Assert.DoesNotContain("limit: '500'", js);
        Assert.Contains("const TX_PAGE_SIZE = ", js);
        Assert.Contains("query.set('after', txCursor);", js);
    }

    /// <summary>
    /// Der Cursor kommt vom Server und wird unveraendert zurueckgereicht. Ihn hier zusammenzusetzen
    /// hiesse, die Sortierregel des Servers ein zweites Mal aufzuschreiben - und die zweite Fassung
    /// waere die, die beim naechsten Umbau vergessen wird.
    /// </summary>
    [Fact]
    public void The_cursor_comes_from_the_server_and_is_never_built_here()
    {
        var js = PageJs();

        Assert.Contains("txCursor = data.hasNext ? (data.nextCursor || null) : null;", js);
        Assert.DoesNotContain("TimelineSortKey", js);
    }

    /// <summary>
    /// Kein COUNT pro Seite: die Antwort auf "gibt es noch mehr" ist hasNext (limit+1), nicht eine
    /// Gesamtzahl. Ein Blaettern, das an einer Gesamtzahl haengt, zahlt bei jedem Nachladen den vollen
    /// Zaehldurchlauf ueber die gefilterte Menge.
    /// </summary>
    [Fact]
    public void Loading_more_is_decided_by_hasNext_and_not_by_a_total()
    {
        var js = PageJs();

        Assert.DoesNotContain("data.total", js);
    }

    /// <summary>
    /// Ein Nachladen darf nicht zu einem zweiten werden, und eine veraltete Antwort darf nicht in eine
    /// inzwischen ganz andere Liste geraten (schneller Konto-/Filterwechsel). Beides haengt an je einer
    /// Wache, die hier steht.
    /// </summary>
    [Fact]
    public void Only_one_request_at_a_time_and_a_stale_answer_is_dropped()
    {
        var js = PageJs();

        Assert.Contains("if (txLoadingMore || !txCursor || !txPageQuery) return;", js);
        Assert.Contains("if (renderId !== listRenderId) return;", js);
        // Und zwar VOR dem Abbau des Beobachters, nicht erst nach dem Abruf: ein ueberholter Aufruf
        // haette ihn sonst abgehaengt und danach ohne ihn zurueckgegeben - die Liste haette ab da
        // nichts mehr nachgeladen, ohne dass irgendetwas kaputt aussieht.
        var start = js.IndexOf("async function loadMoreTransactions(", StringComparison.Ordinal);
        Assert.True(start > 0, "loadMoreTransactions(...) wurde nicht gefunden.");
        var guard = js.IndexOf("if (renderId !== listRenderId) return;", start, StringComparison.Ordinal);
        var disconnect = js.IndexOf("txObserver?.disconnect();", start, StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < disconnect, "Die Nummer wird erst nach dem Abbau des Beobachters geprueft.");
    }

    /// <summary>
    /// Die Stelle, an der der Nutzer gerade liest, darf nicht wegspringen, wenn oben etwas dazukommt.
    /// Gemessen wird dafuer an der ersten bisherigen Zeile und nicht an der Gesamthoehe der Seite: die
    /// Gesamthoehe hat im Versuch auf dem Telefon 689 px zu wenig gemeldet, weil sie an allem haengt,
    /// was sich sonst noch im Dokument setzt.
    /// </summary>
    [Fact]
    public void Prepending_older_rows_keeps_the_reading_position()
    {
        var js = PageJs();

        Assert.Contains("const anchorTop = anchor?.getBoundingClientRect().top ?? 0;", js);
        Assert.Contains("const moved = (anchor?.getBoundingClientRect().top ?? 0) - anchorTop;", js);
        Assert.Contains("if (moved) box.scrollTop += moved;", js);
        Assert.DoesNotContain("box.scrollHeight - before", js);
    }

    /// <summary>
    /// Der Ankerpunkt wandert mit dem Rand der Liste. Bliebe der alte stehen, saesse er nach dem ersten
    /// Nachladen mitten in der Liste und damit dauerhaft im Bild - der Beobachter meldete immer wieder,
    /// und die Seite holte den gesamten Bestand am Stueck. Genau das war im Harness zu sehen: aus 60
    /// Zeilen wurden in einem Rutsch 246.
    /// </summary>
    [Fact]
    public void The_old_sentinel_is_removed_before_a_new_one_is_placed()
    {
        var js = PageJs();

        Assert.Contains("body.querySelectorAll('.tx-more-sentinel').forEach(node => node.remove());", js);
    }

    /// <summary>
    /// Ein Element ohne Flaeche hat keinen Schnittbereich - ob ein IntersectionObserver dafuer ueberhaupt
    /// meldet, unterscheidet sich zwischen Browsern. Beide Ankerpunkte brauchen deshalb eine Hoehe.
    /// </summary>
    [Fact]
    public void Both_sentinels_have_a_height()
    {
        var css = PageCss();

        Assert.Contains(".tx-forecast-sentinel,.tx-more-sentinel{height:1px}", css);
    }

    /// <summary>
    /// Der begrenzte DOM. Uebersprungen werden darf eine Zeile erst, NACHDEM sie einmal wirklich
    /// vermessen wurde: eine uebersprungene Zeile hat nur noch die geschaetzte Hoehe, und mit der
    /// Schaetzung waehrend des Einsetzens zu rechnen hat die Liste im Versuch um 1 632 px verrutschen
    /// lassen. Die Reihenfolge "erst lesen, dann schreiben" gehoert mit dazu - sonst erzwingt jede
    /// einzelne Zeile eine eigene Neuberechnung.
    /// </summary>
    [Fact]
    public void Rows_are_skipped_only_after_their_real_height_was_recorded()
    {
        var js = PageJs();
        var css = PageCss();

        Assert.Contains(".tx-row.tx-parked{content-visibility:auto}", css);
        Assert.Contains("const boxes = rows.map(row => {", js);
        Assert.Contains("row.style.containIntrinsicSize = `auto ${boxes[index]}px`;", js);
        Assert.Contains("row.classList.add('tx-parked');", js);
        // Und zwar die Hoehe des INHALTSKASTENS. Die Zeilenhoehe roh einzutragen liess jede
        // uebersprungene Zeile um Innenabstand und Rahmen wachsen (67 px wurden 90) - die Liste sprang
        // beim ersten Bild um 46 px, gemessen als 0,052 statt 0,008 in LayoutStabilityTests.
        Assert.Contains("- parseFloat(style.paddingTop) - parseFloat(style.paddingBottom)", js);
        Assert.Contains("- parseFloat(style.borderTopWidth) - parseFloat(style.borderBottomWidth);", js);
    }

    /// <summary>
    /// Nach einer Aenderung wird die Liste komplett neu aufgebaut. Mit nur einer Seite waere jemand,
    /// der weit zurueckgescrollt hatte, danach wieder am Anfang - und der Anker, an dem
    /// keepListPosition die Stelle festhaelt, existierte gar nicht mehr. Der Neuaufbau holt deshalb so
    /// viele Zeilen zurueck, wie vorher dastanden, in EINER Anfrage.
    /// </summary>
    [Fact]
    public void A_refresh_brings_back_as_many_rows_as_were_shown()
    {
        var js = PageJs();

        Assert.Contains("const restore = Math.min(Math.max(TX_PAGE_SIZE, txLoadedCount), TX_RESTORE_MAX);", js);
        Assert.Contains("renderTransactions(ctx, { skipTodayScroll: true, limit: restore })", js);
        // Die Folgeseiten bleiben trotzdem normal gross - sonst zoege ein einziges Wiederherstellen
        // die Seitengroesse fuer den Rest der Sitzung hoch.
        Assert.Contains("txPageQuery.set('limit', String(TX_PAGE_SIZE));", js);
    }

    /// <summary>
    /// Eine Seite wird an genau einer Stelle gebaut. Vor #161 stand die Schleife nur inline im ersten
    /// Zeichnen; ein nachgeladener Block haette Kopfzeilen, Vorgemerkt-Gruppe und die Bindungen jeder
    /// Zeile ein zweites Mal beschreiben muessen - und die zweite Beschreibung waere die gewesen, die
    /// irgendwann abweicht.
    /// </summary>
    [Fact]
    public void The_first_page_and_every_later_page_are_built_by_the_same_code()
    {
        var js = PageJs();

        Assert.Contains("function buildRowBlock(items, opts = {})", js);
        Assert.Contains("const fragment = buildRowBlock(items);", js);
        Assert.Contains("const fragment = buildRowBlock(ordered, { ownDays: ascendingTimeline });", js);
    }
}
