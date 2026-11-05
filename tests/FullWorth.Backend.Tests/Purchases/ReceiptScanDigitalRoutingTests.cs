using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Purchases.Extraction;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// A digital eBon is read from its exact text without a model; a scan - even one with an OCR text layer -
/// goes to the model. Which reader read a receipt, in which version and why is stored with the job.
/// </summary>
public sealed class ReceiptScanDigitalRoutingTests
{
    private static readonly string[] PennyReceipt =
    [
        "Penny",
        "Elsbeerenring.2",
        "74392 Freudental",
        "         UID Nr.: DE202748117",
        "                                    EUR",
        "BANANE GELB                       1,07 B",
        "BOURBON VANILLE                   3,29 B",
        "FLUTES KAESE                      1,49 B",
        " App-Coupon 10% Eis             -0,33 B",
        " --------------------------------------",
        " SUMME                   EUR       5,52",
        " ======================================",
        " Geg. VISA               EUR       5,52",
        "  25.06.2026          18:44         Bon-Nr.:9268"
    ];

    // The same text, but its sums do not add up (a digit of the total was misread).
    private static readonly string[] BrokenReceipt = [.. PennyReceipt.Select(line => line.Replace("SUMME                   EUR       5,52", "SUMME                   EUR       6,52"))];

    [Fact]
    public async Task Digital_page_is_recognised_and_a_scan_with_text_layer_is_not()
    {
        if (!PopplerAvailable()) return;
        using var directory = new TempDirectory();
        var digital = directory.Write("digital.pdf", Pdf(PennyReceipt, scannedImage: false));
        var scan = directory.Write("scan.pdf", Pdf(PennyReceipt, scannedImage: true));

        var digitalText = await ReceiptPdfRasterizer.ReadDigitalPageTextAsync(digital, 1, CancellationToken.None);
        Assert.NotNull(digitalText);
        Assert.Contains("SUMME", digitalText, StringComparison.Ordinal);
        Assert.Null(await ReceiptPdfRasterizer.ReadDigitalPageTextAsync(scan, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Digital_pdf_is_read_locally_and_the_reader_and_version_are_stored()
    {
        if (!PopplerAvailable()) return;
        var run = await ProcessAsync(Pdf(PennyReceipt, scannedImage: false));

        Assert.Equal(ReceiptScanJobStatuses.Done, run.Job.Status);
        Assert.Equal(ReceiptReaders.Text, run.Job.Engine);
        Assert.Equal($"text/{ReceiptTextParser.Version}", run.Job.ReaderVersion);
        Assert.Equal(ReceiptRoutes.DigitalPdf, run.Job.Reason);
        Assert.Equal(5.52m, run.TotalAmount);
        Assert.Equal("Penny", run.Merchant);
        Assert.Equal(3, run.ItemCount);
        Assert.Equal(("text", $"text/{ReceiptTextParser.Version}"), (run.RunProvider, run.RunProviderVersion));
    }

    [Fact]
    public async Task Fast_lane_reads_a_digital_pdf_and_leaves_nothing_for_the_ai_lane()
    {
        if (!PopplerAvailable()) return;
        var run = await ProcessAsync(Pdf(PennyReceipt, scannedImage: false), digitalOnly: true);

        Assert.Equal(ReceiptScanJobStatuses.Done, run.Job.Status);
        Assert.Equal(ReceiptReaders.Text, run.Job.Engine);
        Assert.False(run.TakenByAiLane);
        Assert.False(run.TakenByFastLane);
    }

    [Fact]
    public async Task Fast_lane_hands_a_scan_to_the_ai_lane_without_calling_a_model()
    {
        if (!PopplerAvailable()) return;
        var run = await ProcessAsync(Pdf(PennyReceipt, scannedImage: true), digitalOnly: true);

        // Queued again with its road noted, and only the AI lane may claim it now.
        Assert.Equal(ReceiptScanJobStatuses.Queued, run.Job.Status);
        Assert.Equal(ReceiptRoutes.Scan, run.Job.Reason);
        Assert.Null(run.Job.Engine);
        Assert.True(run.TakenByAiLane);
    }

    [Fact]
    public async Task Fast_lane_hands_a_digital_pdf_whose_sums_do_not_add_up_to_the_ai_lane()
    {
        if (!PopplerAvailable()) return;
        var run = await ProcessAsync(Pdf(BrokenReceipt, scannedImage: false), digitalOnly: true);

        Assert.Equal(ReceiptScanJobStatuses.Queued, run.Job.Status);
        Assert.Equal(ReceiptRoutes.DigitalUnverified, run.Job.Reason);
        Assert.True(run.TakenByAiLane);
    }

    [Fact]
    public async Task Finished_job_moves_from_the_queue_to_history_and_review_until_it_is_confirmed()
    {
        if (!PopplerAvailable()) return;
        var run = await ProcessAsync(Pdf(PennyReceipt, scannedImage: false));

        Assert.Equal(0, run.Queue);
        Assert.Equal(1, run.History);
        Assert.Equal(1, run.Review);
        Assert.Equal(1, run.Counts.NeedsReview);
        Assert.Equal(0, run.Counts.Queued + run.Counts.Processing + run.Counts.Failed);
        Assert.Equal(0, run.Failed);
        Assert.Equal(HttpStatusCode.BadRequest, run.UnknownScope);
    }

    [Fact]
    public async Task Digital_pdf_whose_sums_do_not_add_up_goes_to_the_model_with_its_reason()
    {
        if (!PopplerAvailable()) return;
        var run = await ProcessAsync(Pdf(BrokenReceipt, scannedImage: false));

        // No model is configured in the test host, so the OCR road finishes the job - what matters is
        // that the digital text was NOT trusted and the reason says why.
        Assert.NotEqual(ReceiptReaders.Text, run.Job.Engine);
        Assert.Equal(ReceiptRoutes.AiUnavailable, run.Job.Reason);
    }

    [Fact]
    public async Task Scanned_pdf_is_never_read_as_text_even_with_an_ocr_layer()
    {
        if (!PopplerAvailable()) return;
        var run = await ProcessAsync(Pdf(PennyReceipt, scannedImage: true));

        Assert.NotEqual(ReceiptReaders.Text, run.Job.Engine);
        Assert.NotEqual(ReceiptRoutes.DigitalPdf, run.Job.Reason);
    }

    [Fact]
    public void Local_result_is_only_confirmed_without_a_human_when_its_own_sums_agree()
    {
        var good = ReceiptTextParser.Parse(string.Join('\n', PennyReceipt), "EUR");
        var broken = ReceiptTextParser.Parse(string.Join('\n', BrokenReceipt), "EUR");

        Assert.Null(ReceiptAutoConfirmService.RefusalReason(good, []));
        Assert.Equal("low_confidence", ReceiptAutoConfirmService.RefusalReason(broken, []));
        Assert.Equal("warnings", ReceiptAutoConfirmService.RefusalReason(good, ["Hinweis"]));
    }

    private sealed record Run(
        ReceiptScanJobView Job, decimal TotalAmount, string? Merchant, int ItemCount, string? RunProvider, string? RunProviderVersion,
        int Queue, int History, int Review, int Failed, ReceiptScanCounts Counts, HttpStatusCode UnknownScope, bool TakenByAiLane, bool TakenByFastLane);

    private static async Task<Run> ProcessAsync(byte[] pdf, bool digitalOnly = false)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Digital receipt owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Digital receipt space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            await db.SaveChangesAsync();
        });

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new StringContent(jobId.ToString("D")), "clientJobId");
        var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        multipart.Add(file, "receipt", "ebon.pdf");

        using (var create = UserRequest(HttpMethod.Post, $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}", userId, multipart))
        using (var created = await client.SendAsync(create))
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        using (var start = UserRequest(HttpMethod.Post, $"/api/purchases/receipt-scan/jobs/{jobId:D}/start?fullWorthSpaceId={spaceId:D}", userId))
        using (var started = await client.SendAsync(start))
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        // The queue worker is switched off in the test host; claim and process the job by hand.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var claimed = await scope.ServiceProvider.GetRequiredService<ReceiptScanJobStore>().ClaimNextAsync(null, CancellationToken.None);
            Assert.NotNull(claimed);
            await scope.ServiceProvider.GetRequiredService<ReceiptScanQueueProcessor>().ProcessAsync(claimed!, CancellationToken.None, digitalOnly);
        }

        using var read = UserRequest(HttpMethod.Get, $"/api/purchases/receipt-scan/jobs/{jobId:D}?fullWorthSpaceId={spaceId:D}", userId);
        using var response = await client.SendAsync(read);
        var job = JsonSerializer.Deserialize<ReceiptScanJobView>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        (bool Ai, bool Fast) takenBy;
        async Task<int> CountAsync(string scope)
        {
            using var list = UserRequest(HttpMethod.Get, $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}&scope={scope}", userId);
            using var listed = await client.SendAsync(list);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            return JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetArrayLength();
        }
        var (queue, history, review, failed) = (await CountAsync("queue"), await CountAsync("history"), await CountAsync("review"), await CountAsync("failed"));
        using var countsRequest = UserRequest(HttpMethod.Get, $"/api/purchases/receipt-scan/counts?fullWorthSpaceId={spaceId:D}", userId);
        using var countsResponse = await client.SendAsync(countsRequest);
        var counts = JsonSerializer.Deserialize<ReceiptScanCounts>(await countsResponse.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var unknown = UserRequest(HttpMethod.Get, $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}&scope=everything", userId);
        using var unknownResponse = await client.SendAsync(unknown);

        // Which lane would take the job now: only a job handed over by the fast lane is the AI lane's.
        var store = factory.Services.CreateAsyncScope();
        await using (store)
        {
            var jobs = store.ServiceProvider.GetRequiredService<ReceiptScanJobStore>();
            var ai = await jobs.ClaimNextAsync(ReceiptScanLanes.Ai, CancellationToken.None) is not null;
            var fast = await jobs.ClaimNextAsync(null, CancellationToken.None) is not null;
            takenBy = (ai, fast);
        }

        Run? result = null;
        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == job.PurchaseId);
            var itemCount = await db.PurchaseItems.CountAsync(x => x.PurchaseId == job.PurchaseId);
            var attempt = await db.PurchaseExtractionRuns.AsNoTracking().OrderByDescending(x => x.CreatedAt)
                .Where(x => db.PurchaseDocuments.Any(d => d.Id == x.PurchaseDocumentId && d.PurchaseId == job.PurchaseId))
                .FirstOrDefaultAsync();
            result = new Run(job, purchase.TotalAmount, purchase.Merchant, itemCount, attempt?.Provider, attempt?.ProviderVersion,
                queue, history, review, failed, counts, unknownResponse.StatusCode, takenBy.Ai, takenBy.Fast);
        });
        return result!;
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static bool PopplerAvailable() => new[] { "pdfinfo", "pdfimages", "pdftotext", "pdftoppm" }.All(CommandAvailable);

    private static bool CommandAvailable(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                ArgumentList = { "-v" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return false;
            process.WaitForExit(3000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A one-page PDF typeset in Courier. With <paramref name="scannedImage"/> the page is one picture
    /// of the paper and the same text sits on it invisibly - exactly what OCRmyPDF/paperless produce.
    /// </summary>
    private static byte[] Pdf(IReadOnlyList<string> lines, bool scannedImage)
    {
        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var content = new StringBuilder();
        if (scannedImage) content.Append("q 283 0 0 842 0 0 cm /Im1 Do Q\n");
        content.Append("BT /F1 8 Tf 10 TL 10 830 Td\n");
        if (scannedImage) content.Append("3 Tr\n");
        foreach (var line in lines) content.Append('(').Append(Escape(line)).Append(") '\n");
        content.Append("ET\n");

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 283 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>"
                + (scannedImage ? " /XObject << /Im1 6 0 R >>" : "") + " >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"
        };
        if (scannedImage)
            objects.Add("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length 9 >>\nstream\nFFFFFFFF>\nendstream");

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string path = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"fw-digital-{Guid.NewGuid():N}")).FullName;

        public string Write(string name, byte[] bytes)
        {
            var file = Path.Combine(path, name);
            File.WriteAllBytes(file, bytes);
            return file;
        }

        public void Dispose() => Directory.Delete(path, recursive: true);
    }
}
