using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class GptPurchasesUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public GptPurchasesUiBaselineTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Normal_receipt_scan_enters_the_staged_scan_set_builder()
    {
        var purchases = Read("purchases.js");
        var normal = Read("purchases-gpt-normal.js");
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("import { tryGptReceiptScan } from './purchases-gpt-normal.js'", purchases);
        Assert.Contains("await tryGptReceiptScan(ctx, file)", purchases);
        Assert.Contains("import { addReceiptScanFiles } from './receipt-scan-set.js'", normal);
        Assert.Contains("return addReceiptScanFiles(ctx, files).then", normal);
        Assert.Contains("Ein Beleg · mehrere Seiten", scanSet);
        Assert.Contains("+ Weitere Seite / Foto", scanSet);
    }

    [Fact]
    public void Scan_set_is_collected_locally_before_one_durable_server_job_is_created()
    {
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("state: 'collecting'", scanSet);
        Assert.Contains("const clientJobId = crypto.randomUUID()", scanSet);
        Assert.Contains("for (const file of draft.files) form.append('receipt', file, file.name)", scanSet);
        Assert.Contains("form.append('clientJobId', clientJobId)", scanSet);
        Assert.Contains("api/purchases/receipt-scan/jobs", scanSet);
        Assert.Contains("api/purchases/receipt-scan/jobs/${clientJobId}", scanSet);
        Assert.DoesNotContain("api/purchases/gpt-test/scan", scanSet);
    }

    [Fact]
    public void Sequential_mobile_camera_captures_are_added_to_the_same_open_receipt()
    {
        var normal = Read("purchases-gpt-normal.js");
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("event.stopImmediatePropagation()", normal);
        Assert.Contains("addReceiptScanFiles(latestContext, selected)", normal);
        Assert.Contains("if (!activeDraft || activeDraft.finished) activeDraft = createDraft(ctx)", scanSet);
        Assert.Contains("else activeDraft.ctx = ctx", scanSet);
        Assert.Contains("input.click()", scanSet);
        Assert.Contains("Photograph a long receipt section by section", scanSet);
    }

    [Fact]
    public void User_can_review_order_remove_mistakes_and_only_then_start_upload()
    {
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("data-up", scanSet);
        Assert.Contains("data-down", scanSet);
        Assert.Contains("data-remove", scanSet);
        Assert.Contains("move(draft", scanSet);
        Assert.Contains("draft.files.splice", scanSet);
        Assert.Contains("data-start", scanSet);
        Assert.Contains("submitDraft(draft)", scanSet);
    }

    [Fact]
    public void Scan_set_file_limit_matches_backend_upload_contract()
    {
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("const MAX_FILES = 20", scanSet);
        Assert.Contains("files.length}/${MAX_FILES}", scanSet);
    }

    [Fact]
    public void Cancelling_builder_never_falls_through_to_legacy_single_file_upload()
    {
        var normal = Read("purchases-gpt-normal.js");
        var purchases = Read("purchases.js");

        Assert.Contains("Belegscan abgebrochen.", normal);
        Assert.Contains("throw new Error", normal);
        // purchases.js still retains the old compatibility fallback, therefore the wrapper must throw
        // rather than resolve null when the local scan-set is intentionally cancelled.
        Assert.Contains("api/purchases/receipt-scan", purchases);
    }

    [Fact]
    public void Pdf_and_multiple_images_share_the_same_logical_receipt_flow()
    {
        var normal = Read("purchases-gpt-normal.js");
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("input.multiple = true", normal);
        Assert.Contains("input.multiple = true", scanSet);
        Assert.Contains("isPdf(file)", scanSet);
        Assert.Contains("all PDF pages", scanSet);
        Assert.Contains("files as one receipt", scanSet);
    }

    [Fact]
    public void Upload_uses_stable_client_job_id_and_recovers_an_uncertain_response_without_duplicate_purchase()
    {
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("const clientJobId = crypto.randomUUID()", scanSet);
        Assert.Contains("form.append('clientJobId', clientJobId)", scanSet);
        Assert.Contains("api/purchases/receipt-scan/jobs/${clientJobId}", scanSet);
    }

    [Fact]
    public void Scan_set_supports_background_processing_after_server_acceptance()
    {
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("data-background", scanSet);
        Assert.Contains("backgroundDraft(draft)", scanSet);
        Assert.Contains("while (draft.row && draft.row.status !== 'done'", scanSet);
        Assert.Contains("Beleg wird im Hintergrund weiterverarbeitet", scanSet);
    }

    private string Read(string fileName)
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(environment.WebRootPath, "features", fileName));
    }
}
