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

        Assert.Contains("import { initializePurchaseEnhancements, tryGptReceiptScan } from './purchases-gpt-normal.js'", purchases);
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

    /// <summary>
    /// One page is the normal case and gets a view without the multi-page apparatus — but it keeps the
    /// two things that are not furniture: the preview, because a blurry capture caught here costs one
    /// tap instead of a whole extraction, and "add page", because that is what turns a single camera
    /// capture into a multi-page receipt. Drop either and the shorter view costs a capability.
    /// </summary>
    [Fact]
    public void A_single_page_keeps_its_preview_and_the_way_to_add_another()
    {
        var scanSet = Read("receipt-scan-set.js");

        Assert.Contains("const single = files.length === 1", scanSet);
        Assert.Contains("function singleRow(file)", scanSet);
        Assert.Contains("data-file-preview=\"0\"", scanSet);

        // The add-page button sits outside the single/multi branch, so it is rendered once for both.
        var addRow = scanSet.IndexOf("receipt-set-add-row", StringComparison.Ordinal);
        var branch = scanSet.IndexOf("const single = files.length === 1", StringComparison.Ordinal);
        Assert.True(addRow > branch, "the add-page row must be rendered after the branch, for both views");
        Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(scanSet, @"data-add>"),
            match => true);
    }

    /// <summary>
    /// Closing the scan set is an answer, not a fault.
    ///
    /// The rejection itself has to stay — returning null would let the caller fall through to the legacy
    /// single-file upload, so cancelling would upload the photo anyway, which is what
    /// <see cref="Cancelling_builder_never_falls_through_to_legacy_single_file_upload"/> guards. So the
    /// cancel carries a name and the caller stays quiet for it instead of showing it where errors go.
    /// </summary>
    [Fact]
    public void Cancelling_the_scan_set_is_not_reported_as_an_error()
    {
        Assert.Contains("cancelled.name = 'ReceiptScanCancelled'", Read("purchases-gpt-normal.js"));
        Assert.Contains("err?.name !== 'ReceiptScanCancelled'", Read("purchases.js"));
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
        // The rule is "reject, never resolve falsy". It used to be checked as the literal text
        // "throw new Error", so naming the error broke the test while the rule had not moved.
        Assert.Contains("if (result) return result;", normal);
        Assert.Contains("throw cancelled", normal);
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
