using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

public sealed class ReceiptImportIntegrationTests
{
    [Fact]
    public async Task BulkUploadCreatesOnePurchasePerFileAndRetryIsIdempotent()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        using (var request = UploadRequest(spaceId, userId, batchId,
                   ("first.png", Png(1)),
                   ("second.png", Png(2))))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("queued").GetInt32());
            var jobs = json.RootElement.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("receiptScanJobId").GetGuid())
                .ToArray();
            Assert.Equal(2, jobs.Distinct().Count());
        }

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(2, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
        });

        using (var retry = UploadRequest(spaceId, userId, batchId,
                   ("first.png", Png(1)),
                   ("second.png", Png(2))))
        using (var response = await client.SendAsync(retry))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(batchId, json.RootElement.GetProperty("batch").GetProperty("id").GetGuid());
            Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
        }

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(2, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
        });
    }

    [Fact]
    public async Task BulkUploadOfOneHundredOneFilesCreatesOneHundredOneIndependentJobsAndPurchases()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        var files = Enumerable.Range(0, 101)
            .Select(index => ($"receipt-{index:D3}.png", Png((byte)index)))
            .ToArray();
        using var request = UploadRequest(spaceId, userId, Guid.NewGuid(), files);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(101, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(101, json.RootElement.GetProperty("queued").GetInt32());
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(101, items.Select(x => x.GetProperty("receiptScanJobId").GetGuid()).Distinct().Count());
        Assert.Equal(101, items.Select(x => x.GetProperty("purchaseId").GetGuid()).Distinct().Count());

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(101, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
        });
    }

    [Fact]
    public async Task BulkUploadMultiPagePdfRemainsOneJobWithMultipleSources()
    {
        if (!CommandAvailable("pdfinfo")) return;

        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        using var request = UploadRequest(spaceId, userId, Guid.NewGuid(), ("two-pages.pdf", MinimalPdf(2)));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        var jobId = item.GetProperty("receiptScanJobId").GetGuid();
        var purchaseId = item.GetProperty("purchaseId").GetGuid();

        using var sourcesRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources?fullWorthSpaceId={spaceId:D}");
        AddUserHeaders(sourcesRequest, userId);
        using var sourcesResponse = await client.SendAsync(sourcesRequest);
        Assert.Equal(HttpStatusCode.OK, sourcesResponse.StatusCode);
        using var sources = JsonDocument.Parse(await sourcesResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, sources.RootElement.GetArrayLength());
        Assert.Equal(new[] { 1, 2 }, sources.RootElement.EnumerateArray().Select(x => x.GetProperty("pageNumber").GetInt32()).ToArray());

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.Id == purchaseId));
            var document = await db.PurchaseDocuments.SingleAsync(x => x.PurchaseId == purchaseId);
            Assert.Equal(2, document.PageCount);
            Assert.Equal("application/pdf", document.MediaType);
        });
    }

    [Fact]
    public async Task AutoStartMovesReceiptOutOfDraftQueueState()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        using var request = UploadRequest(spaceId, userId, Guid.NewGuid(), true, ("auto.png", Png(9)));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.NotEqual("draft", item.GetProperty("jobStatus").GetString());
        Assert.True(item.GetProperty("receiptScanJobId").GetGuid() != Guid.Empty);
    }

    [Fact]
    public async Task InvalidFileFailsOnlyItsOwnItem()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        using var request = UploadRequest(spaceId, userId, Guid.NewGuid(),
            ("valid.png", Png(3)),
            ("invalid.txt", new byte[] { 1, 2, 3, 4 }));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("queued").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("failed").GetInt32());

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
        });
    }

    [Fact]
    public async Task NonMemberCannotCreateOrReadReceiptImportBatches()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var memberId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, memberId, spaceId, outsiderId);

        using (var request = UploadRequest(spaceId, outsiderId, Guid.NewGuid(), ("receipt.png", Png(4))))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var list = new HttpRequestMessage(HttpMethod.Get, $"/api/purchases/receipt-imports/batches?fullWorthSpaceId={spaceId:D}");
        AddUserHeaders(list, outsiderId);
        using var listResponse = await client.SendAsync(list);
        Assert.Equal(HttpStatusCode.NotFound, listResponse.StatusCode);
    }

    [Fact]
    public async Task ExactContentInAnotherBatchIsSkippedBeforeSecondReceiptIsCreated()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);
        var bytes = Png(5);

        using (var first = UploadRequest(spaceId, userId, Guid.NewGuid(), ("first.png", bytes)))
        using (var firstResponse = await client.SendAsync(first))
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using (var second = UploadRequest(spaceId, userId, Guid.NewGuid(), ("same-again.png", bytes)))
        using (var secondResponse = await client.SendAsync(second))
        {
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            using var json = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("skippedDuplicates").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("queued").GetInt32());
        }

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
        });
    }

    [Fact]
    public async Task FolderRepeatImportSkipsPreviouslyImportedFingerprint()
    {
        var inbox = Path.Combine(Path.GetTempPath(), "fullworth-receipt-inbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inbox);
        var source = Path.Combine(inbox, "receipt.png");
        await File.WriteAllBytesAsync(source, Png(6));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(-10));

        try
        {
            using var factory = new BackendWebApplicationFactory(new Dictionary<string, string?>
            {
                ["ReceiptImports:FolderEnabled"] = "true",
                ["ReceiptImports:InboxPath"] = inbox,
                ["ReceiptImports:FolderRecursive"] = "true",
                ["ReceiptImports:FolderStableAgeSeconds"] = "1",
                ["ReceiptImports:AutoStart"] = "false"
            });
            using var client = factory.CreateClient();
            var userId = Guid.NewGuid();
            var spaceId = Guid.NewGuid();
            await SeedMemberAsync(factory, userId, spaceId);

            using (var first = FolderImportRequest(spaceId, userId))
            using (var firstResponse = await client.SendAsync(first))
            {
                Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
                using var json = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
                Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("queued").GetInt32());
                Assert.Equal(0, json.RootElement.GetProperty("skippedDuplicates").GetInt32());
            }

            using (var second = FolderImportRequest(spaceId, userId))
            using (var secondResponse = await client.SendAsync(second))
            {
                Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
                using var json = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
                Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
                Assert.Equal(0, json.RootElement.GetProperty("queued").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("skippedDuplicates").GetInt32());
            }

            await factory.SeedAsync(async db =>
            {
                Assert.Equal(1, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
            });
        }
        finally
        {
            try { Directory.Delete(inbox, recursive: true); } catch { }
        }
    }

    private static HttpRequestMessage UploadRequest(
        Guid fullWorthSpaceId,
        Guid userId,
        Guid batchId,
        params (string FileName, byte[] Content)[] files) =>
        UploadRequest(fullWorthSpaceId, userId, batchId, false, files);

    private static HttpRequestMessage UploadRequest(
        Guid fullWorthSpaceId,
        Guid userId,
        Guid batchId,
        bool autoStart,
        params (string FileName, byte[] Content)[] files)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new StringContent(autoStart ? "true" : "false"), "autoStart");
        multipart.Add(new StringContent(batchId.ToString("D")), "clientBatchId");
        foreach (var file in files)
        {
            var content = new ByteArrayContent(file.Content);
            content.Headers.ContentType = new MediaTypeHeaderValue(file.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "text/plain");
            multipart.Add(content, "receipts", file.FileName);
        }

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/purchases/receipt-imports/upload?fullWorthSpaceId={fullWorthSpaceId:D}")
        {
            Content = multipart
        };
        AddUserHeaders(request, userId);
        return request;
    }

    private static HttpRequestMessage FolderImportRequest(Guid fullWorthSpaceId, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/purchases/receipt-imports/folder/import?fullWorthSpaceId={fullWorthSpaceId:D}")
        {
            Content = JsonContent.Create(new { currency = "EUR", autoStart = false })
        };
        AddUserHeaders(request, userId);
        return request;
    }

    private static void AddUserHeaders(HttpRequestMessage request, Guid userId)
    {
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
    }

    private static async Task SeedMemberAsync(
        BackendWebApplicationFactory factory,
        Guid memberId,
        Guid spaceId,
        Guid? additionalUserId = null)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = memberId,
                EmailNormalized = $"{memberId:N}@EXAMPLE.COM",
                DisplayName = "Receipt import member",
                IsActive = true
            });
            if (additionalUserId.HasValue)
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = additionalUserId.Value,
                    EmailNormalized = $"{additionalUserId.Value:N}@EXAMPLE.COM",
                    DisplayName = "Receipt import outsider",
                    IsActive = true
                });
            }
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Receipt Import Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = memberId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();

            // LegacyParityCapabilityAuthorizationMiddleware gates every non-GET /api/purchases write with
            // purchases.manage; a plain member resolves to the read-only viewer template, so grant the
            // editor template (which carries purchases.manage). Only the member is granted.
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, memberId);
        });
    }

    private static byte[] Png(byte marker) => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, marker, 0, 0, 0 };

    private static byte[] MinimalPdf(int pageCount)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>"
        };
        var pageRefs = Enumerable.Range(0, pageCount).Select(index => $"{3 + index * 2} 0 R");
        objects.Add($"<< /Type /Pages /Kids [{string.Join(' ', pageRefs)}] /Count {pageCount} >>");
        for (var index = 0; index < pageCount; index++)
        {
            var pageObject = 3 + index * 2;
            var contentObject = pageObject + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentObject} 0 R >>");
            objects.Add("<< /Length 0 >>\nstream\n\nendstream");
        }

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Count + 1];
        for (var index = 0; index < objects.Count; index++)
        {
            offsets[index + 1] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n");
        builder.Append("0000000000 65535 f \n");
        for (var index = 1; index < offsets.Length; index++)
            builder.Append(offsets[index].ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        builder.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
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
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
