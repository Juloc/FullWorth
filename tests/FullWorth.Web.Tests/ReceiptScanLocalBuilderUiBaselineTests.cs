using FullWorth.Web.Tests.Pwa;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class ReceiptScanLocalBuilderUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public ReceiptScanLocalBuilderUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Normal_scan_collects_locally_before_durable_queue_upload()
    {
        var normal = Read("pages", "purchases", "gpt-normal.js");
        // The old features/receipt-scan-local-builder.js was an unreachable patch layer that handed
        // locally-collected files to a *separate* durable dialog (features/receipt-scan-ai.js) via a
        // DataTransfer/native-input trick, then used a MutationObserver (autoStartDurableDraft) to
        // auto-click that other dialog's Start button once it appeared. Both files were removed as dead
        // code ("Remove unreachable frontend patch layer"); receipt-scan-set.js is the one reachable
        // module (wired from gpt-normal.js via addReceiptScanFiles) and now owns durable job
        // creation itself instead of handing off to another dialog.
        var builder = Read("pages", "purchases", "receipt-scan-set.js");

        Assert.Contains("addReceiptScanFiles", normal);
        Assert.Contains("const MAX_FILES = 20", builder);
        Assert.Contains("+ Weitere Seite / Foto", builder);
        Assert.Contains("data-remove", builder);
        Assert.Contains("data-up", builder);
        Assert.Contains("data-down", builder);
        // No DataTransfer/auto-click hand-off needed anymore (that whole MutationObserver pattern is now
        // forbidden for new files by FrontendArchitectureGuardTests.NoNewGlobalDomPatchObservers): the
        // module uploads the ordered local files and creates the durable ReceiptScanJob itself.
        Assert.Contains("for (const file of draft.files) form.append('receipt', file, file.name)", builder);
        Assert.Contains("draft.row = await draft.ctx.api('api/purchases/receipt-scan/jobs'", builder);
        Assert.Contains("async function submitDraft(draft)", builder);
    }

    [Fact]
    public void Cancelling_local_builder_never_calls_durable_scanner()
    {
        var builder = Read("pages", "purchases", "receipt-scan-set.js");
        var cancelStart = builder.IndexOf("function cancelDraft(draft)", StringComparison.Ordinal);
        var nextFunction = builder.IndexOf("function closeDialog(draft)", cancelStart, StringComparison.Ordinal);
        var cancelBody = builder[cancelStart..nextFunction];

        Assert.Contains("draft.finished = true", cancelBody);
        // The old cancel(draft) rejected the promise directly so purchases.js's legacy single-file
        // fallback could never run. Now cancelling resolves the draft promise with null instead; the
        // "must not fall through to the legacy flow" guarantee moved one layer up, into the
        // tryGptReceiptScan wrapper in gpt-normal.js, which turns a null result into a thrown
        // Error (see GptPurchasesUiBaselineTests.Cancelling_builder_never_falls_through_to_legacy_single_file_upload).
        Assert.Contains("draft.resolve?.(null)", cancelBody);
        Assert.DoesNotContain("submitDraft", cancelBody);
        Assert.DoesNotContain("draft.ctx.api", cancelBody);
    }

    /// <summary>
    /// Waehrend der Verarbeitung blieb vom fotografierten Beleg eine Liste von Dateinamen uebrig -
    /// das Bild lag direkt daneben und wurde weggeworfen (#129). Es bleibt jetzt stehen, und darueber
    /// steht, WELCHE Strecke laeuft: Texterkennung oder KI.
    /// </summary>
    [Fact]
    public void The_receipt_stays_visible_while_it_is_processed()
    {
        var builder = Read("pages", "purchases", "receipt-scan-set.js");
        var start = builder.IndexOf("function renderProgress(draft)", StringComparison.Ordinal);
        var end = builder.IndexOf("function engineLine(draft)", start, StringComparison.Ordinal);
        var progress = builder[start..end];

        // Dieselbe Vorschau wie beim Sammeln, nicht eine Liste von Namen.
        Assert.Contains("data-file-preview", progress);
        Assert.Contains("singleRow(draft.files[0])", progress);
        Assert.Contains("hydratePreviews(dialog, draft.files)", progress);
        Assert.DoesNotContain("receipt-set-sources compact", builder);

        // Und die Strecke steht da, in beide Richtungen.
        Assert.Contains("keine KI beteiligt", builder);
        Assert.Contains("no AI involved", builder);
        Assert.Contains("KI-Analyse (GPT) liest den Beleg", builder);
        Assert.Contains("data-engine", progress);
    }

    /// <summary>
    /// Ein Fehler beendet den Vorgang nicht mehr: die Dateien liegen noch da, der Auftrag oft auch.
    /// Wichtig ist dabei, dass ein zweiter Versuch WEITER beobachtet statt neu hochzuladen - sonst
    /// entstuenden aus einem Einkauf zwei Belege.
    /// </summary>
    [Fact]
    public void A_failed_scan_can_be_retried_without_uploading_twice()
    {
        var builder = Read("pages", "purchases", "receipt-scan-set.js");

        Assert.Contains("data-retry", builder);
        Assert.Contains("Erneut versuchen", builder);
        Assert.Contains("function retryDraft(draft)", builder);
        Assert.Contains("if (draft.row?.id)", builder);
        Assert.Contains("void followDraft(draft)", builder);

        // Der Fehler meldet sich erst nach aussen, wenn der Benutzer aufgibt - vorher steht der
        // Dialog noch und bietet den zweiten Versuch an.
        var failStart = builder.IndexOf("function failDraft(draft, error)", StringComparison.Ordinal);
        var failEnd = builder.IndexOf("function renderProgress(draft)", failStart, StringComparison.Ordinal);
        var fail = builder[failStart..failEnd];
        Assert.DoesNotContain("draft.reject", fail);
        Assert.DoesNotContain("draft.finished = true", fail);
        Assert.Contains("draft.reject?.(error)", builder[builder.IndexOf("function abandonDraft(draft)", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Service_worker_precaches_local_builder()
    {
        var sw = Read("sw.js");
        Assert.Matches(@"const\s+VERSION\s*=\s*'v\d+'", sw);
        PwaAssert.Ships("/pages/purchases/receipt-scan-set.js", sw);
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
