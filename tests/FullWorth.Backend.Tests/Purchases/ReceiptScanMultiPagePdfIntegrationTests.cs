using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ReceiptScanMultiPagePdfIntegrationTests
{
    // Small valid 3-page PDF fixture. It is intentionally generated content rather than a real receipt
    // so the acceptance test contains no personal/financial fixture data.
    private const string ThreePagePdfBase64 = "JVBERi0xLjMKJZOMi54gUmVwb3J0TGFiIEdlbmVyYXRlZCBQREYgZG9jdW1lbnQgKG9wZW5zb3VyY2UpCjEgMCBvYmoKPDwKL0YxIDIgMCBSCj4+CmVuZG9iagoyIDAgb2JqCjw8Ci9CYXNlRm9udCAvSGVsdmV0aWNhIC9FbmNvZGluZyAvV2luQW5zaUVuY29kaW5nIC9OYW1lIC9GMSAvU3VidHlwZSAvVHlwZTEgL1R5cGUgL0ZvbnQKPj4KZW5kb2JqCjMgMCBvYmoKPDwKL0NvbnRlbnRzIDkgMCBSIC9NZWRpYUJveCBbIDAgMCAyMDkuNzYzOCAyOTcuNjM3OCBdIC9QYXJlbnQgOCAwIFIgL1Jlc291cmNlcyA8PAovRm9udCAxIDAgUiAvUHJvY1NldCBbIC9QREYgL1RleHQgL0ltYWdlQiAvSW1hZ2VDIC9JbWFnZUkgXQo+PiAvUm90YXRlIDAgL1RyYW5zIDw8Cgo+PiAKICAvVHlwZSAvUGFnZQo+PgplbmRvYmoKNCAwIG9iago8PAovQ29udGVudHMgMTAgMCBSIC9NZWRpYUJveCBbIDAgMCAyMDkuNzYzOCAyOTcuNjM3OCBdIC9QYXJlbnQgOCAwIFIgL1Jlc291cmNlcyA8PAovRm9udCAxIDAgUiAvUHJvY1NldCBbIC9QREYgL1RleHQgL0ltYWdlQiAvSW1hZ2VDIC9JbWFnZUkgXQo+PiAvUm90YXRlIDAgL1RyYW5zIDw8Cgo+PiAKICAvVHlwZSAvUGFnZQo+PgplbmRvYmoKNSAwIG9iago8PAovQ29udGVudHMgMTEgMCBSIC9NZWRpYUJveCBbIDAgMCAyMDkuNzYzOCAyOTcuNjM3OCBdIC9QYXJlbnQgOCAwIFIgL1Jlc291cmNlcyA8PAovRm9udCAxIDAgUiAvUHJvY1NldCBbIC9QREYgL1RleHQgL0ltYWdlQiAvSW1hZ2VDIC9JbWFnZUkgXQo+PiAvUm90YXRlIDAgL1RyYW5zIDw8Cgo+PiAKICAvVHlwZSAvUGFnZQo+PgplbmRvYmoKNiAwIG9iago8PAovUGFnZU1vZGUgL1VzZU5vbmUgL1BhZ2VzIDggMCBSIC9UeXBlIC9DYXRhbG9nCj4+CmVuZG9iago3IDAgb2JqCjw8Ci9BdXRob3IgKGFub255bW91cykgL0NyZWF0aW9uRGF0ZSAoRDoyMDI2MDgzMTA4NTczMCswMCcwMCcpIC9DcmVhdG9yIChhbm9ueW1vdXMpIC9LZXl3b3JkcyAoKSAvTW9kRGF0ZSAoRDoyMDI2MDgzMTA4NTczMCswMCcwMCcpIC9Qcm9kdWNlciAoUmVwb3J0TGFiIFBERiBMaWJyYXJ5IC0gXChvcGVuc291cmNlXCkpIAogIC9TdWJqZWN0ICh1bnNwZWNpZmllZCkgL1RpdGxlICh1bnRpdGxlZCkgL1RyYXBwZWQgL0ZhbHNlCj4+CmVuZG9iago4IDAgb2JqCjw8Ci9Db3VudCAzIC9LaWRzIFsgMyAwIFIgNCAwIFIgNSAwIFIgXSAvVHlwZSAvUGFnZXMKPj4KZW5kb2JqCjkgMCBvYmoKPDwKL0ZpbHRlciBbIC9BU0NJSTg1RGVjb2RlIC9GbGF0ZURlY29kZSBdIC9MZW5ndGggOTUKPj4Kc3RyZWFtCkdhcFFoMEU9RiwwVVxIM1RccE5ZVF5RS2s/dGM+SVAsO1cjVTFeMjNpaFBFTV8/Q1c0S0lTaF4mZEFPSStoN3B1IUskJz8kQU91ViteIy1xPXMuQWA+UUM5YCY/LH4+ZW5kc3RyZWFtCmVuZG9iagoxMCAwIG9iago8PAovRmlsdGVyIFsgL0FTQ0lJODVEZWNvZGUgL0ZsYXRlRGVjb2RlIF0gL0xlbmd0aCA5NQo+PgpzdHJlYW0KR2FwUWgwRT1GLDBVXEgzVFxwTllUXlFLaz90Yz5JUCw7VyNVMV4yM2loUEVNXz9DVzRLSVNoXiZkQU9JK2g3cHUhSyQnPyQ6XkhrK14jLXE9cy5BYD5RQzluJj81fj5lbmRzdHJlYW0KZW5kb2JqCjExIDAgb2JqCjw8Ci9GaWx0ZXIgWyAvQVNDSUk4NURlY29kZSAvRmxhdGVEZWNvZGUgXSAvTGVuZ3RoIDk1Cj4+CnN0cmVhbQpHYXBRaDBFPUYsMFVcSDNUXHBOWVReUUtrP3RjPklQLDtXI1UxXjIzaWhQRU1fP0NXNEtJU2heJmRBT0kraDdwdSFLJCc/JEhBTUErXiMtcT1zLkFgPlFDOicmPz5+PmVuZHN0cmVhbQplbmRvYmoKeHJlZgowIDEyCjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDA2MSAwMDAwMCBuIAowMDAwMDAwMDkyIDAwMDAwIG4gCjAwMDAwMDAxOTkgMDAwMDAgbiAKMDAwMDAwMDQwMiAwMDAwMCBuIAowMDAwMDAwNjA2IDAwMDAwIG4gCjAwMDAwMDA4MTAgMDAwMDAgbiAKMDAwMDAwMDg3OCAwMDAwMCBuIAowMDAwMDAxMTM5IDAwMDAwIG4gCjAwMDAwMDEyMTAgMDAwMDAgbiAKMDAwMDAwMTM5NCAwMDAwMCBuIAowMDAwMDAxNTc5IDAwMDAwIG4gCnRyYWlsZXIKPDwKL0lEIApbPDUyZTNjMTU4NjRmMTJmMWQyMzNiMzNjMTdmNTU4YmNiPjw1MmUzYzE1ODY0ZjEyZjFkMjMzYjMzYzE3ZjU1OGJjYj5dCiUgUmVwb3J0TGFiIGdlbmVyYXRlZCBQREYgZG9jdW1lbnQgLS0gZGlnZXN0IChvcGVuc291cmNlKQoKL0luZm8gNyAwIFIKL1Jvb3QgNiAwIFIKL1NpemUgMTIKPj4Kc3RhcnR4cmVmCjE3NjQKJSVFT0YK";

    [Fact]
    public async Task ThreePagePdfCreatesOnePurchaseOneDocumentAndThreeOrderedSources()
    {
        // PDF expansion shells out to poppler (pdfinfo/pdftoppm), installed in the production backend image.
        // Developer machines without them can still run the rest of the suite, mirroring ReceiptScanPdfIntegrationTests.
        if (!CommandAvailable("pdfinfo") || !CommandAvailable("pdftoppm")) return;

        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var rootSourceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new StringContent(jobId.ToString("D")), "clientJobId");
        multipart.Add(new ByteArrayContent(Convert.FromBase64String(ThreePagePdfBase64)), "receipt", "three-pages.pdf");
        multipart.Add(new StringContent(rootSourceId.ToString("D")), "sourceId");
        using var request = UserRequest(HttpMethod.Post, $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}", userId, multipart);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var purchaseId = body.RootElement.GetProperty("purchaseId").GetGuid();
        Assert.Equal(3, body.RootElement.GetProperty("sourceCount").GetInt32());
        Assert.Equal("draft", body.RootElement.GetProperty("status").GetString());

        using var sourceRequest = UserRequest(HttpMethod.Get, $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources?fullWorthSpaceId={spaceId:D}", userId);
        using var sourceResponse = await client.SendAsync(sourceRequest);
        Assert.Equal(HttpStatusCode.OK, sourceResponse.StatusCode);
        using var sourcesJson = JsonDocument.Parse(await sourceResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, sourcesJson.RootElement.GetArrayLength());
        Assert.Equal(new[] { 1, 2, 3 }, sourcesJson.RootElement.EnumerateArray().Select(x => x.GetProperty("pageNumber").GetInt32()).ToArray());
        Assert.All(sourcesJson.RootElement.EnumerateArray(), x => Assert.Equal("pdf_page", x.GetProperty("sourceType").GetString()));
        var documentIds = sourcesJson.RootElement.EnumerateArray().Select(x => x.GetProperty("purchaseDocumentId").GetGuid()).Distinct().ToArray();
        Assert.Single(documentIds);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.Id == purchaseId));
            var document = await db.PurchaseDocuments.SingleAsync(x => x.Id == documentIds[0]);
            Assert.Equal(purchaseId, document.PurchaseId);
            Assert.Equal(3, document.PageCount);
            Assert.Equal("application/pdf", document.MediaType);
            var sourceRows = await db.Database.SqlQuery<SourceRow>($"""
                SELECT "Id", "SortOrder", "PageNumber" FROM "ReceiptScanSources"
                WHERE "ReceiptScanJobId" = {jobId}
                ORDER BY "SortOrder"
                """).ToListAsync();
            Assert.Equal(3, sourceRows.Count);
            Assert.Equal(new[] { 0, 1, 2 }, sourceRows.Select(x => x.SortOrder).ToArray());
            Assert.Equal(new int?[] { 1, 2, 3 }, sourceRows.Select(x => x.PageNumber).ToArray());
        });
    }

    private static async Task SeedMemberAsync(BackendWebApplicationFactory factory, Guid userId, Guid spaceId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "PDF member", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "PDF Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member resolves to the read-only viewer template,
            // so grant editor (carrying purchases.manage) to reach the receipt-scan queue write handler.
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
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

    private sealed record SourceRow(Guid Id, int SortOrder, int? PageNumber);
}
