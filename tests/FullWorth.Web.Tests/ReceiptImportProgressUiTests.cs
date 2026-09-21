using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #129: beim Belegimport sichtbar machen, was gerade passiert - und womit.
///
/// Der Kamera-Pfad (<c>receipt-scan-set.js</c>) konnte das laengst: er zeigt die Stufe des
/// Scan-Jobs im Klartext und unterscheidet KI von lokalem OCR. Der MASSENIMPORT zeigte nur den
/// groben Status ("wird verarbeitet"), obwohl derselbe Scan-Job dieselben Stufen fuehrt - und dort
/// ist es wichtiger, weil es unbeaufsichtigt laeuft und "haengt das oder rechnet es" die einzige
/// Frage ist, die man dann hat.
///
/// Keine Migration: <c>ReceiptScanJobs.Stage</c> und <c>.Engine</c> gab es schon, und der Job war in
/// den Abfragen bereits gejoint. Es fehlten zwei Spalten in der Projektion und die Anzeige.
/// </summary>
public sealed class ReceiptImportProgressUiTests
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

    private static string Backend(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root(), "src", "FullWorth.Backend", .. parts]));

    private static string Details() => Asset("pages", "purchases", "receipt-import-batch-details.js");

    /// <summary>Die Stufe kommt aus dem ohnehin gejointen Scan-Job - kein zweiter Abruf, keine neue Tabelle.</summary>
    [Fact]
    public void The_backend_carries_the_scan_stage_into_the_import_list()
    {
        var store = Backend("Modules", "Purchases", "ReceiptImports", "ReceiptImportStore.cs");

        Assert.Contains("j.\"Stage\" AS \"JobStage\"", store);
        Assert.Contains("j.\"Engine\" AS \"JobEngine\"", store);
        // In allen Abfragen, die eine Beleg-Zeile bauen - sonst zeigt eine Ansicht die Stufe und die
        // daneben nicht, je nachdem welcher Weg sie geladen hat.
        Assert.Equal(4, Regex.Matches(store, @"j\.""Stage"" AS ""JobStage""").Count);
        Assert.Contains("string? JobStage = null", Backend("Modules", "Purchases", "ReceiptImports", "ReceiptImportModels.cs"));
    }

    /// <summary>
    /// Der laufende Schritt nur waehrend der Verarbeitung: bei einem fertigen Beleg ist die letzte
    /// Stufe keine Auskunft mehr, und daneben steht ohnehin, was herauskam.
    /// </summary>
    [Fact]
    public void The_running_step_is_shown_only_while_it_is_running()
    {
        var js = Details();

        Assert.Contains("status === 'processing' && item.jobStage", js);
        Assert.Contains("status !== 'processing' && item.jobEngine", js);
    }

    /// <summary>
    /// Das Issue verlangt ausdruecklich, dass keine KI-Nutzung suggeriert wird, wo keine war - und
    /// dass nach Abschluss nachvollziehbar bleibt, welche Verarbeitung gelaufen ist.
    /// </summary>
    [Fact]
    public void Ai_and_local_ocr_are_named_apart()
    {
        var js = Details();
        var body = js[js.IndexOf("function engineLabel", StringComparison.Ordinal)..];

        Assert.Contains("codex", body);
        Assert.Contains("tesseract", body);
        Assert.Contains("KI-Analyse", body);
        Assert.Contains("OCR", body);
        // Eine unbekannte Engine faellt auf ihren eigenen Namen zurueck, statt als KI zu gelten.
        Assert.Contains("return engine;", body);
    }

    /// <summary>
    /// Keine erfundenen Prozentwerte, wie das Issue es verlangt: gezeigt wird der Schrittname, den der
    /// Server fuehrt, und sonst nichts.
    /// </summary>
    [Fact]
    public void No_invented_progress_percentage()
    {
        var js = Details();

        Assert.DoesNotContain("%", js[js.IndexOf("function stageLabel", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("Math.random", js);
    }

    /// <summary>
    /// Jede Stufe, die der Server schreibt, hat einen Text. Eine unbekannte faellt auf eine allgemeine
    /// Formulierung zurueck statt auf den rohen Schluessel - ein Nutzer soll nicht "structuring" lesen.
    /// </summary>
    [Fact]
    public void Every_stage_the_server_writes_has_a_label()
    {
        var js = Details();
        var body = js[js.IndexOf("function stageLabel", StringComparison.Ordinal)..];

        foreach (var stage in new[] { "queued", "preparing", "connecting", "analyzing", "structuring", "ocr", "saving" })
            Assert.Contains($"{stage}:", body);
        Assert.Contains("labels[stage] ||", body);
    }
}
