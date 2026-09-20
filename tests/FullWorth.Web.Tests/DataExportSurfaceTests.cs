using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #135, der Export: <c>api/export/wealth-backup</c> war verlinkt, <c>csv-zip</c>, <c>xlsx</c>,
/// <c>snapshot</c> und <c>wealth-full</c> nicht - obwohl der CSV-Export am 2026-09-15 noch von zwei
/// N+1-Schleifen befreit wurde. Gepflegter Code, der nie lief.
///
/// Eingebaut wurden CSV und XLSX: zwei Formate, die ein Mensch woanders oeffnet. <c>wealth-full</c>
/// bleibt bewusst draussen - es ist dieselbe Quelle wie <c>wealth-backup</c>
/// (<c>WealthPortableExportService.BuildAsync</c> statt <c>BackupAsync</c>), also dieselben Daten in
/// einer zweiten Verpackung; ein zweiter Knopf dafuer waere eine Auswahl ohne Unterschied. Diese
/// Entscheidung steht hier fest, damit sie nicht unbemerkt in "vergessen" umkippt.
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

    [Fact]
    public void The_csv_and_xlsx_exports_are_reachable_from_the_settings_page()
    {
        var html = Asset("pages", "settings", "page.html");
        var js = Asset("pages", "settings", "page.js");

        foreach (var id in new[] { "export-data", "export-csv", "export-xlsx" })
        {
            Assert.Contains($"id=\"{id}\"", html);
            Assert.Contains($"ctx.$('#{id}')", js);
        }

        Assert.Contains("api/export/csv-zip", Portability());
        Assert.Contains("api/export/xlsx", Portability());
    }

    /// <summary>
    /// Drei Ziele, ein Download-Weg. Sperre, Dateiname aus <c>content-disposition</c>, Blob-Link und
    /// Aufraeumen sind bei allen dasselbe - dreimal kopiert waere es dreimal zu pflegen, und der
    /// naechste Fix landete in einer der drei Kopien.
    /// </summary>
    [Fact]
    public void All_three_exports_share_one_download_path()
    {
        var js = Portability();

        Assert.Single(Regex.Matches(js, @"URL\.createObjectURL"));
        // Bewusst die Code-Form und nicht das blosse Wort: "content-disposition" steht auch im
        // Kommentar darueber, und ein Test, der Prosa mitzaehlt, misst nicht, was er behauptet.
        Assert.Single(Regex.Matches(js, @"headers\.get\('content-disposition'\)"));
        Assert.Single(Regex.Matches(js, @"apiClient\.backendResponse"));
        // Und genau drei Aufrufer dieses einen Wegs.
        Assert.Equal(3, Regex.Matches(js, @"return downloadExport\(ctx, button, \{").Count);
    }

    /// <summary>
    /// Die Sperre gilt ueber alle Ziele, nicht je Knopf: die drei Zeilen stehen untereinander, und zwei
    /// gleichzeitige Exporte desselben Bestands waeren nur doppelte Serverarbeit.
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

        Assert.Contains("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", js);
        Assert.Equal(2, Regex.Matches(js, @"accept: 'application/zip'").Count);
        Assert.Contains(".xlsx`", js);
        Assert.Equal(2, Regex.Matches(js, @"\.zip`").Count);
    }

    /// <summary>
    /// Der Hinweis unter "Vollständige Sicherung" versprach JSON, der Knopf lud seit jeher eine ZIP.
    /// Ein Text, der das falsche Format nennt, ist genauso ein Fehler wie ein fehlender Knopf - nur
    /// einer, den niemand meldet.
    /// </summary>
    [Fact]
    public void The_backup_row_no_longer_claims_to_download_json()
    {
        foreach (var locale in new[] { "de", "en" })
        {
            var json = Asset("locales", $"{locale}.json");
            var export = json[json.IndexOf("\"export\"", StringComparison.Ordinal)..];
            export = export[..export.IndexOf('}')];
            Assert.DoesNotContain("JSON", export);
            foreach (var key in new[] { "csvTitle", "csvHint", "xlsxTitle", "xlsxHint" })
                Assert.Contains($"\"{key}\"", export);
        }
    }

    /// <summary>
    /// Bewusst nicht eingebaut. Faellt dieser Test, hat jemand <c>wealth-full</c> doch verlinkt - dann
    /// gehoert die Begruendung oben ueberdacht, nicht dieser Test geloescht.
    /// </summary>
    [Fact]
    public void The_json_twin_of_the_backup_stays_out_of_the_ui()
    {
        Assert.DoesNotContain("api/export/wealth-full", Portability());
        Assert.DoesNotContain("wealth-full", Asset("pages", "settings", "page.js"));
    }
}
