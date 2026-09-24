namespace FullWorth.Web.Tests;

/// <summary>
/// Der Weg zur Sicherungspruefung (#177).
///
/// <c>POST /api/import/wealth-backup/validate</c> stand fertig im Baum und hatte keinen Aufrufer -
/// und davor sogar zweimal, Zeile fuer Zeile gleich, einmal unter <c>/api/export</c> und einmal
/// unter <c>/api/import</c>. Uebrig ist der eine Weg, und jetzt fuehrt auch einer hin.
///
/// Der wichtigste Satz dieses Tests ist der letzte: geprueft, nicht eingespielt. Einen
/// Wiederherstellungs-Endpunkt gibt es in diesem Stand nicht, und ein Knopf namens "Sicherung" in
/// einer Anwendung, die nicht zurueckspielen kann, ist genau die Art Versprechen, die man erst im
/// Ernstfall prueft. Der Dialog sagt das; dieser Test haelt fest, dass er es weiter sagt.
/// </summary>
public sealed class BackupCheckUiTests
{
    private static string Modul() => WebSources.Asset("features", "wealth-portability.js");

    [Fact]
    public void The_check_is_reachable_from_the_data_settings()
    {
        Assert.Contains("id=\"check-backup\"", WebSources.Page("Settings"), StringComparison.Ordinal);
        Assert.Contains("openBackupCheckDialog", WebSources.Asset("pages", "settings", "page.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void It_posts_the_archive_to_the_one_remaining_route()
    {
        var quelle = Modul();

        Assert.Contains("api/import/wealth-backup/validate", quelle, StringComparison.Ordinal);
        // Der Rumpf ist die Datei selbst, nicht ein FormData-Umschlag: der Endpunkt liest den
        // Anfragekoerper direkt als ZIP.
        Assert.Contains("body: chosen", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("new FormData", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Gueltig" ueber null geprueften Dokumenten bedeutet etwas anderes als ueber zweihundert, und
    /// eine Warnung ist kein Beiwerk - sie ist oft der einzige Hinweis, dass die Sicherung weniger
    /// enthaelt als jemand glaubt.
    /// </summary>
    [Fact]
    public void The_verdict_names_what_was_actually_checked()
    {
        var quelle = Modul();

        Assert.Contains("verdict.documentsChecked", quelle, StringComparison.Ordinal);
        Assert.Contains("verdict.schemaVersion", quelle, StringComparison.Ordinal);
        Assert.Contains("verdict.errors", quelle, StringComparison.Ordinal);
        Assert.Contains("verdict.warnings", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fehlertexte kommen vom Server und gehen ueber <c>textContent</c>. Ueber Markup waeren sie eine
    /// Einladung, und der Inhalt einer hochgeladenen Datei entscheidet mit, was darin steht.
    /// </summary>
    [Fact]
    public void Server_text_never_goes_through_markup()
    {
        var quelle = Modul();
        var abschnitt = quelle[quelle.IndexOf("function paint(verdict)", StringComparison.Ordinal)..];

        Assert.DoesNotContain("innerHTML", abschnitt, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", abschnitt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Dialog sagt, dass FullWorth eine Sicherung NICHT einspielen kann. Sobald es einen
    /// Wiederherstellungs-Endpunkt gibt, gehoert dieser Satz weg - bis dahin gehoert er dorthin, wo
    /// jemand ihn liest, und nicht in eine Fussnote.
    /// </summary>
    [Fact]
    public void The_dialog_says_that_restoring_is_not_possible_yet()
    {
        var quelle = Modul();

        Assert.Contains("kann FullWorth eine Sicherung noch nicht", quelle, StringComparison.Ordinal);
        Assert.Contains("cannot restore a backup yet", quelle, StringComparison.Ordinal);

        // Und es gibt ihn wirklich nicht: gaebe es eine Wiederherstellungsroute, waere der Satz
        // falsch und dieser Test der Ort, an dem das auffaellt.
        var surface = File.ReadAllText(Path.Combine(WebSources.RepoRoot,
            "tests", "FullWorth.Backend.Tests", "Architecture", "route-surface.txt"));
        Assert.DoesNotContain("wealth-backup/restore", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("wealth-backup/import", surface, StringComparison.Ordinal);
    }
}
