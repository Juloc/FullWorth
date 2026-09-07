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
        var normal = Read("features", "purchases-gpt-normal.js");
        // The old features/receipt-scan-local-builder.js was an unreachable patch layer that handed
        // locally-collected files to a *separate* durable dialog (features/receipt-scan-ai.js) via a
        // DataTransfer/native-input trick, then used a MutationObserver (autoStartDurableDraft) to
        // auto-click that other dialog's Start button once it appeared. Both files were removed as dead
        // code ("Remove unreachable frontend patch layer"); receipt-scan-set.js is the one reachable
        // module (wired from purchases-gpt-normal.js via addReceiptScanFiles) and now owns durable job
        // creation itself instead of handing off to another dialog.
        var builder = Read("features", "receipt-scan-set.js");

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
        var builder = Read("features", "receipt-scan-set.js");
        var cancelStart = builder.IndexOf("function cancelDraft(draft)", StringComparison.Ordinal);
        var nextFunction = builder.IndexOf("function closeDialog(draft)", cancelStart, StringComparison.Ordinal);
        var cancelBody = builder[cancelStart..nextFunction];

        Assert.Contains("draft.finished = true", cancelBody);
        // The old cancel(draft) rejected the promise directly so purchases.js's legacy single-file
        // fallback could never run. Now cancelling resolves the draft promise with null instead; the
        // "must not fall through to the legacy flow" guarantee moved one layer up, into the
        // tryGptReceiptScan wrapper in purchases-gpt-normal.js, which turns a null result into a thrown
        // Error (see GptPurchasesUiBaselineTests.Cancelling_builder_never_falls_through_to_legacy_single_file_upload).
        Assert.Contains("draft.resolve?.(null)", cancelBody);
        Assert.DoesNotContain("submitDraft", cancelBody);
        Assert.DoesNotContain("draft.ctx.api", cancelBody);
    }

    [Fact]
    public void Service_worker_precaches_local_builder()
    {
        var sw = Read("sw.js");
        Assert.Matches(@"const\s+VERSION\s*=\s*'v\d+'", sw);
        Assert.Contains("/features/receipt-scan-set.js", sw);
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
