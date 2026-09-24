namespace FullWorth.Web.Tests;

/// <summary>
/// #131, Schritt 2 in der Oberflaeche: erst die Datei, dann die Quelle.
///
/// Quelltextpruefungen ohne Browser. Was hier festgehalten wird, ist die Entscheidung dahinter: dass
/// die Datei nur EINMAL gewaehlt und nur einmal hochgeladen wird, dass jeder erkannte Weg auch
/// wirklich angesteuert wird, und dass eine Datei, die sich nicht zuordnen laesst, keinen Weg oeffnet.
/// </summary>
public sealed class ImportSourceDetectionUiTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string PageJs() => File.ReadAllText(Path.Combine(
        Root(), "src", "FullWorth.Web", "wwwroot", "pages", "settings", "import", "page.js"));

    private static string PageHtml() => File.ReadAllText(Path.Combine(
        Root(), "src", "FullWorth.Web", "Pages", "Settings", "Import", "Index.cshtml"));

    /// <summary>
    /// Die Dateiauswahl steht VOR den Quellenkacheln. Umgekehrt hiess es: erst raten, welche von fuenf
    /// Quellen die richtige ist, und den Irrtum danach an einer Fehlermeldung ueber die Datei merken.
    /// </summary>
    [Fact]
    public void The_file_comes_first_and_the_source_tiles_second()
    {
        var html = PageHtml();

        var detect = html.IndexOf("id=\"detect-file\"", StringComparison.Ordinal);
        var tiles = html.IndexOf("import-provider-grid", StringComparison.Ordinal);
        Assert.True(detect > 0, "Die Dateiauswahl fehlt.");
        Assert.True(tiles > detect, "Die Quellenkacheln stehen vor der Dateiauswahl.");
    }

    /// <summary>
    /// Der Anhang deckt alles ab, was irgendein Weg lesen kann - sonst laesst sich die Datei, die
    /// FullWorth eigentlich versteht, im Dateidialog gar nicht erst auswaehlen.
    /// </summary>
    [Theory]
    [InlineData(".csv")]
    [InlineData(".xlsx")]
    [InlineData(".xml")]
    [InlineData(".sta")]
    [InlineData(".mt940")]
    [InlineData(".pdf")]
    public void Every_readable_format_can_be_picked(string extension)
    {
        var html = PageHtml();
        var start = html.IndexOf("id=\"detect-file\"", StringComparison.Ordinal);
        var accept = html[start..html.IndexOf('>', start)];

        Assert.Contains(extension, accept, StringComparison.Ordinal);
    }

    /// <summary>
    /// Jeder der fuenf erkennbaren Wege wird auch angesteuert. Ein Adapter ohne Ziel hiesse: die
    /// Erkennung sagt richtig, was die Datei ist, und danach passiert nichts.
    /// </summary>
    [Theory]
    [InlineData("finanzguru", "navigate('import-finanzguru-xlsx')")]
    [InlineData("broker-pdf", "navigate('import-broker-pdf')")]
    public void A_detected_source_on_its_own_page_is_actually_opened(string adapter, string navigation)
    {
        var js = PageJs();

        Assert.Contains($"detection.adapter==='{adapter}'", js, StringComparison.Ordinal);
        Assert.Contains(navigation, js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_detected_source_on_this_page_switches_to_its_mode_and_runs_the_analysis()
    {
        var js = PageJs();

        Assert.Contains("activateMode(mode);", js, StringComparison.Ordinal);
        Assert.Contains("$(mode==='statement'?'stmt-detect':mode==='investments'?'inv-detect':'tx-detect')?.click();", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Datei wird nicht zweimal hochgeladen und nicht zweimal ausgewaehlt: sie liegt noch im
    /// Browser, und jede Zielseite ist eine Ansicht im selben Dokument. Uebergeben wird das
    /// File-Objekt - ueber eine DataTransfer-Liste, weil ein File-Input anders nicht befuellbar ist.
    /// </summary>
    [Fact]
    public void The_file_is_handed_over_instead_of_being_chosen_again()
    {
        var js = PageJs();

        Assert.Contains("const transfer=new DataTransfer();", js, StringComparison.Ordinal);
        Assert.Contains("input.files=transfer.files;", js, StringComparison.Ordinal);
        Assert.Contains("handOverFile('finanzguru-file',file)", js, StringComparison.Ordinal);
        Assert.Contains("handOverFile('pdf-file',file)", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Was sich nicht zuordnen laesst, bekommt keinen Knopf. Ein "Weiter", das irgendwohin fuehrt,
    /// waere schlimmer als keines - es oeffnet den falschen Weg mit voller Ueberzeugung.
    /// </summary>
    [Fact]
    public void An_unknown_file_gets_an_explanation_and_no_button()
    {
        var js = PageJs();
        var start = js.IndexOf("function renderDetection(", StringComparison.Ordinal);
        Assert.True(start > 0, "renderDetection() wurde nicht gefunden.");
        var body = js[start..js.IndexOf("\n}", start, StringComparison.Ordinal)];

        var unknownBranch = body.IndexOf("detection.adapter==='unknown'", StringComparison.Ordinal);
        var button = body.IndexOf("createElement('button')", StringComparison.Ordinal);
        Assert.True(unknownBranch > 0 && button > unknownBranch, "Der Knopf entsteht nicht erst im Sonst-Zweig.");
        Assert.Contains("t.detectUnknown", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Grund steht als Schluessel in der Antwort, der Satz in der Oberflaeche. Der Server kennt die
    /// Sprache des Nutzers nicht und soll sie nicht kennen muessen.
    /// </summary>
    [Fact]
    public void The_server_answers_with_a_reason_key_and_this_page_writes_the_sentence()
    {
        var js = PageJs();

        Assert.Contains("const REASON_TEXT=", js, StringComparison.Ordinal);
        foreach (var key in new[] { "finanzguruHeaders", "mt940", "camt", "pdf", "investmentColumns", "tabularColumns", "unmappedColumns", "unreadable", "noRows" })
            Assert.Contains(key + ":()=>", js, StringComparison.Ordinal);
    }
}
