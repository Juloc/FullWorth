using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #135, der Export: <c>api/export/wealth-backup</c> war verlinkt, <c>csv-zip</c>, <c>xlsx</c>,
/// <c>snapshot</c> und <c>wealth-full</c> nicht - obwohl der CSV-Export am 2026-09-15 noch von zwei
/// N+1-Schleifen befreit wurde. Gepflegter Code, der nie lief.
///
/// Erreichbar sind jetzt alle fuenf - aber ueber EINE Zeile mit einer Auswahl, nicht ueber fuenf
/// Zeilen. Der urspruengliche Einwand gegen <c>wealth-full</c> und <c>snapshot</c> galt einem zweiten
/// KNOPF fuer dieselben Daten in einer anderen Verpackung, und der besteht weiter: es gibt keinen.
/// Es gibt zwei Fragen - was, und in welcher Form -, und JSON ist dort die Antwort fuer den, der die
/// Daten weiterverarbeitet statt sichert.
/// </summary>
public sealed class DataExportSurfaceTests
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

    private static string Portability() => Asset("features", "wealth-portability.js");

    /// <summary>
    /// Alle fuenf Export-Routen sind erreichbar - ueber EINE Zeile.
    ///
    /// Hier standen drei Knoepfe. Das war ein Fortschritt gegenueber "zwei davon gar nicht", aber
    /// nicht die Antwort: <c>export/wealth-full</c> und <c>export/snapshot</c> fehlten weiterhin, und
    /// fuenf Zeilen fuer im Kern dieselben Daten waeren die falsche Richtung gewesen. Es sind zwei
    /// Fragen - was, und in welcher Form.
    /// </summary>
    [Fact]
    public void All_five_export_routes_are_reachable_through_one_entry()
    {
        var html = Asset("pages", "settings", "page.html");
        var js = Asset("pages", "settings", "page.js");

        Assert.Contains("id=\"export-data\"", html);
        Assert.Contains("ctx.$('#export-data')", js);
        // Und nur diese eine. Die zwei alten Zeilen sind weg, nicht versteckt.
        Assert.DoesNotContain("export-csv", html);
        Assert.DoesNotContain("export-xlsx", html);

        foreach (var route in new[] { "wealth-backup", "wealth-full", "csv-zip", "xlsx", "snapshot" })
            Assert.Contains($"api/export/{route}", Portability());
    }

    /// <summary>
    /// Die Auswahl fuehrt: eine Sicherung gibt es nicht als Excel, eine Tabelle nicht als
    /// wiederherstellbares ZIP. Und der Unterschied steht sichtbar daneben - wer eine Tabelle zieht
    /// und glaubt, ein Backup zu haben, merkt es erst, wenn er es braucht.
    /// </summary>
    [Fact]
    public void The_dialog_only_offers_formats_that_exist_and_says_what_can_be_restored()
    {
        var js = Portability();

        Assert.Contains("EXPORT_FORMATS", js);
        Assert.Contains("backup: [['zip', 'ZIP'], ['json', 'JSON']]", js);
        Assert.Contains("tables: [['csv', 'CSV'], ['xlsx', 'Excel'], ['json', 'JSON']]", js);
        Assert.Contains("wieder einspielen", js);
        Assert.Contains("nicht wiederherstellbar", js);
    }

    /// <summary>
    /// Fuenf Ziele, ein Download-Weg. Sperre, Dateiname aus <c>content-disposition</c>, Blob-Link und
    /// Aufraeumen sind bei allen dasselbe - fuenfmal kopiert waere es fuenfmal zu pflegen, und der
    /// naechste Fix landete in einer der Kopien.
    /// </summary>
    [Fact]
    public void All_five_exports_share_one_download_path()
    {
        var js = Portability();

        Assert.Single(Regex.Matches(js, @"URL\.createObjectURL"));
        // Bewusst die Code-Form und nicht das blosse Wort: "content-disposition" steht auch im
        // Kommentar darueber, und ein Test, der Prosa mitzaehlt, misst nicht, was er behauptet.
        Assert.Single(Regex.Matches(js, @"headers\.get\('content-disposition'\)"));
        Assert.Single(Regex.Matches(js, @"apiClient\.backendResponse"));
        // Und genau EIN Aufrufer: der Dialog holt das Ziel aus der Tabelle, statt fuenf Funktionen zu
        // haben, die sich nur in drei Zeichenketten unterscheiden.
        Assert.Single(Regex.Matches(js, @"downloadExport\(ctx, null, \{"));
        // Fuenf Ziele, fuenf Pfade - keines ist als Kopie danebengewachsen.
        Assert.Equal(5, Regex.Matches(js, @"path: 'api/export/").Count);
    }

    /// <summary>
    /// Die Sperre gilt ueber alle Ziele, nicht je Auswahl: zwei gleichzeitige Exporte desselben
    /// Bestands waeren nur doppelte Serverarbeit.
    /// </summary>
    [Fact]
    public void One_export_at_a_time_across_all_targets()
    {
        var js = Portability();

        Assert.Contains("let exporting = false;", js);
        Assert.Contains("if (!space || exporting) return;", js);
        // Kein Zustand je Ziel, der die gemeinsame Sperre aushebeln wuerde.
        Assert.DoesNotContain("exportingCsv", js);
        Assert.DoesNotContain("exportingXlsx", js);
    }

    /// <summary>
    /// Jedes Ziel schickt den Accept-Header, den sein Endpunkt wirklich beantwortet, und faellt auf
    /// einen Dateinamen mit der passenden Endung zurueck. Eine .zip fuer eine Arbeitsmappe waere eine
    /// Datei, die sich beim Doppelklick nicht oeffnet.
    /// </summary>
    [Fact]
    public void Every_export_declares_the_format_its_endpoint_actually_returns()
    {
        var js = Portability();

        // Je Ziel gehoeren Accept-Header und Dateiendung zusammen - sie stehen deshalb in DERSELBEN
        // Zeile bzw. demselben Eintrag. Eine .zip fuer eine Arbeitsmappe waere eine Datei, die sich
        // beim Doppelklick nicht oeffnet.
        Assert.Contains("accept: 'application/zip', extension: 'zip'", js);
        Assert.Contains("accept: 'application/json', extension: 'json'", js);
        Assert.Contains("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", js);
        Assert.Contains("extension: 'xlsx'", js);

        // Die Endung kommt aus dem Ziel und wird nicht je Aufrufer noch einmal geschrieben - genau
        // dort gingen die beiden sonst auseinander.
        Assert.Single(Regex.Matches(js, @"\$\{target\.extension\}"));
    }

    /// <summary>
    /// Der Text der Zeile kommt aus den Sprachdateien, nicht aus dem Markup: <c>data-i18n</c>
    /// ueberschreibt beim Zeichnen, was im HTML steht. Wer nur das Markup aendert, sieht die
    /// Aenderung genau bis zum ersten Sprachwechsel.
    ///
    /// Der Test hiess "no_longer_claims_to_download_json" und verbot das Wort JSON - damals richtig,
    /// weil die Zeile eine ZIP lud und JSON versprach. Jetzt fuehrt dieselbe Zeile zu einer Auswahl,
    /// in der JSON eines von drei Formaten ist; das Wort gehoert hinein.
    /// </summary>
    [Fact]
    public void The_one_export_row_is_named_the_same_in_markup_and_in_both_languages()
    {
        Assert.Contains("data-i18n=\"export.title\"", Asset("pages", "settings", "page.html"));

        foreach (var (locale, title) in new[] { ("de", "Daten exportieren"), ("en", "Export data") })
        {
            var json = Asset("locales", $"{locale}.json");
            var export = json[json.IndexOf("\"export\"", StringComparison.Ordinal)..];
            export = export[..export.IndexOf('}')];

            Assert.Contains(title, export);
            // Die vier Texte der geloeschten Zeilen sind mitgegangen - ein Schluessel ohne Zeile ist
            // genau die Art Rest, die spaeter niemand mehr zuordnen kann.
            foreach (var key in new[] { "csvTitle", "csvHint", "xlsxTitle", "xlsxHint" })
                Assert.DoesNotContain($"\"{key}\"", export);
        }
    }

    /// <summary>
    /// Hier stand das Gegenteil: <c>wealth-full</c> sei bewusst NICHT eingebaut, weil ein zweiter
    /// Knopf fuer dieselben Daten in einem anderen Behaelter nur Ballast waere. Der Test sagte selbst,
    /// was zu tun ist, wenn er faellt - die Begruendung ueberdenken, nicht ihn loeschen.
    ///
    /// Genau das ist passiert. Der Einwand galt einem zweiten KNOPF, und den gibt es nicht: es gibt
    /// eine Zeile und darin eine Formatauswahl. JSON ist dort kein Ballast, sondern die Antwort fuer
    /// den, der die Daten weiterverarbeitet statt sichert.
    /// </summary>
    [Fact]
    public void The_json_variants_are_reachable_but_never_as_their_own_row()
    {
        var html = Asset("pages", "settings", "page.html");
        var js = Portability();

        Assert.Contains("api/export/wealth-full", js);
        Assert.Contains("api/export/snapshot", js);

        // Kein eigener Einstieg - weder als Zeile in den Einstellungen noch als zweite Funktion.
        Assert.DoesNotContain("wealth-full", html);
        Assert.DoesNotContain("snapshot", html);
        Assert.Single(Regex.Matches(js, @"export function "));
    }
}
