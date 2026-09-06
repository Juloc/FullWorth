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
        var builder = Read("features", "receipt-scan-local-builder.js");

        // The normal scan routes into the staged local collector before any durable upload. After the
        // purchase-articles merge the wired entry point is addReceiptScanFiles (see GptPurchasesUiBaselineTests),
        // which stages every file locally exactly like runReceiptScanSet in the precached local builder below.
        Assert.Contains("addReceiptScanFiles", normal);
        Assert.Contains("const MAX_FILES = 20", builder);
        Assert.Contains("+ Weitere Seite / Foto", builder);
        Assert.Contains("data-remove", builder);
        Assert.Contains("data-up", builder);
        Assert.Contains("data-down", builder);
        Assert.Contains("new DataTransfer()", builder);
        Assert.Contains("runReceiptScanExperience", builder);
        Assert.Contains("autoStartDurableDraft", builder);
    }

    [Fact]
    public void Cancelling_local_builder_never_calls_durable_scanner()
    {
        var builder = Read("features", "receipt-scan-local-builder.js");
        var cancelStart = builder.IndexOf("function cancel(draft)", StringComparison.Ordinal);
        var nextFunction = builder.IndexOf("async function hydratePreviews", cancelStart, StringComparison.Ordinal);
        var cancelBody = builder[cancelStart..nextFunction];

        Assert.Contains("draft.finished = true", cancelBody);
        Assert.Contains("draft.reject", cancelBody);
        Assert.DoesNotContain("runReceiptScanExperience", cancelBody);
        Assert.DoesNotContain("ctx.api", cancelBody);
    }

    [Fact]
    public void Service_worker_precaches_local_builder()
    {
        var sw = Read("sw.js");
        Assert.Matches(@"const\s+VERSION\s*=\s*'v\d+'", sw);
        Assert.Contains("/features/receipt-scan-local-builder.js", sw);
    }

    private string Read(params string[] path)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
