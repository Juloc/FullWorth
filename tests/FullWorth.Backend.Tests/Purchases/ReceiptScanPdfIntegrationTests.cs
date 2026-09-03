using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ReceiptScanPdfIntegrationTests
{
    // Minimal valid two-page PDF generated once for this deterministic integration fixture.
    private static readonly byte[] TwoPagePdf = Convert.FromBase64String(
        "JVBERi0xLjMKJeLjz9MKMSAwIG9iago8PAovUHJvZHVjZXIgKHB5cGRmKQo+PgplbmRvYmoKMiAwIG9iago8PAovVHlwZSAvUGFnZXMKL0NvdW50IDIKL0tpZHMgWyA0IDAgUiA1IDAgUiBdCj4+CmVuZG9iagozIDAgb2JqCjw8Ci9UeXBlIC9DYXRhbG9nCi9QYWdlcyAyIDAgUgo+PgplbmRvYmoKNCAwIG9iago8PAovVHlwZSAvUGFnZQovUmVzb3VyY2VzIDw8Cj4+Ci9NZWRpYUJveCBbIDAuMCAwLjAgNzIgNzIgXQovUGFyZW50IDIgMCBSCj4+CmVuZG9iago1IDAgb2JqCjw8Ci9UeXBlIC9QYWdlCi9SZXNvdXJjZXMgPDwKPj4KL01lZGlhQm94IFsgMC4wIDAuMCA3MiA3MiBdCi9QYXJlbnQgMiAwIFIKPj4KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMDU0IDAwMDAwIG4gCjAwMDAwMDAxMTkgMDAwMDAgbiAKMDAwMDAwMDE2OCAwMDAwMCBuIAowMDAwMDAwMjYwIDAwMDAwIG4gCnRyYWlsZXIKPDwKL1NpemUgNgovUm9vdCAzIDAgUgovSW5mbyAxIDAgUgo+PgpzdGFydHhyZWYKMzUyCiUlRU9GCg==");

    [Fact]
    public async Task TwoPagePdfExpandsToTwoOrderedSourcesInsideOnePurchase()
    {
        // Queue PDF expansion deliberately uses the same poppler tools installed in the production
        // backend image. Developer machines without them can still run the rest of the test suite.
        if (!CommandAvailable("pdfinfo") || !CommandAvailable("pdftoppm")) return;

        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "PDF receipt owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "PDF receipt space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new StringContent(jobId.ToString("D")), "clientJobId");
        var pdf = new ByteArrayContent(TwoPagePdf);
        pdf.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        multipart.Add(pdf, "receipt", "long-receipt.pdf");

        using var request = UserRequest(HttpMethod.Post,
            $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}", userId, multipart);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var purchaseId = body.RootElement.GetProperty("purchaseId").GetGuid();
        Assert.Equal(jobId, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(2, body.RootElement.GetProperty("sourceCount").GetInt32());

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == spaceId));
            var sources = await db.Database.SqlQuery<ReceiptScanSourceRow>($"""
                SELECT "Id", "ReceiptScanJobId", "SortOrder", "SourceType", "OriginalFileName", "MimeType",
                       "StoragePath", "PageNumber", "Fingerprint", "SizeBytes", "CreatedAt"
                FROM "ReceiptScanSources"
                WHERE "ReceiptScanJobId" = {jobId}
                ORDER BY "SortOrder"
                """).ToListAsync();

            Assert.Equal(2, sources.Count);
            Assert.Equal(new[] { 0, 1 }, sources.Select(x => x.SortOrder).ToArray());
            Assert.Equal(new int?[] { 1, 2 }, sources.Select(x => x.PageNumber).ToArray());
            Assert.All(sources, source => Assert.Equal("pdf_page", source.SourceType));
            Assert.All(sources, source => Assert.Equal("application/pdf", source.MimeType));
            Assert.All(sources, source => Assert.Equal("long-receipt.pdf", source.OriginalFileName));
        });

        using var sourceRequest = UserRequest(HttpMethod.Get,
            $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources?fullWorthSpaceId={spaceId:D}", userId);
        using var sourceResponse = await client.SendAsync(sourceRequest);
        Assert.Equal(HttpStatusCode.OK, sourceResponse.StatusCode);
        using var sourceJson = JsonDocument.Parse(await sourceResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, sourceJson.RootElement.GetArrayLength());
        Assert.Equal(1, sourceJson.RootElement[0].GetProperty("pageNumber").GetInt32());
        Assert.Equal(2, sourceJson.RootElement[1].GetProperty("pageNumber").GetInt32());

        using var galleryRequest = UserRequest(HttpMethod.Get,
            $"/api/purchases/{purchaseId:D}/receipt?fullWorthSpaceId={spaceId:D}", userId);
        using var galleryResponse = await client.SendAsync(galleryRequest);
        Assert.Equal(HttpStatusCode.OK, galleryResponse.StatusCode);
        var gallery = await galleryResponse.Content.ReadAsStringAsync();
        Assert.Contains("2 Seiten/Bilder", gallery, StringComparison.Ordinal);
        Assert.Contains("Seite 1", gallery, StringComparison.Ordinal);
        Assert.Contains("Seite 2", gallery, StringComparison.Ordinal);
    }

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

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
