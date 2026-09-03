using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ReceiptScanQueueIntegrationTests
{
    [Fact]
    public async Task ThreeImagesCreateOneDurableDraftOnePurchaseAndStartAsOneJob()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var clientJobId = Guid.NewGuid();
        var sourceIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await SeedMemberAsync(factory, userId, spaceId);

        using var request = QueueRequest(spaceId, userId, clientJobId,
            ("top.png", ImageBytes(1), sourceIds[0]),
            ("middle.png", ImageBytes(2), sourceIds[1]),
            ("bottom.png", ImageBytes(3), sourceIds[2]));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobId = json.RootElement.GetProperty("id").GetGuid();
        var purchaseId = json.RootElement.GetProperty("purchaseId").GetGuid();
        Assert.Equal(clientJobId, jobId);
        Assert.Equal("draft", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("draft", json.RootElement.GetProperty("stage").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("sourceCount").GetInt32());

        // Lost-response retry returns the exact committed set instead of appending the three files again.
        using var retry = QueueRequest(spaceId, userId, clientJobId,
            ("top.png", ImageBytes(1), sourceIds[0]),
            ("middle.png", ImageBytes(2), sourceIds[1]),
            ("bottom.png", ImageBytes(3), sourceIds[2]));
        using var retryResponse = await client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        using var retryJson = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
        Assert.Equal(jobId, retryJson.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(purchaseId, retryJson.RootElement.GetProperty("purchaseId").GetGuid());
        Assert.Equal(3, retryJson.RootElement.GetProperty("sourceCount").GetInt32());

        await factory.SeedAsync(async db =>
        {
            var purchase = Assert.Single(await db.Purchases.AsNoTracking().Where(x => x.FullWorthSpaceId == spaceId).ToListAsync());
            Assert.Equal(purchaseId, purchase.Id);
            Assert.Equal("captured", purchase.Status);
            Assert.Equal("needs_review", purchase.ReviewState);
            Assert.Equal(userId, purchase.CreatedByUserId);
            Assert.Equal("space", purchase.Visibility);

            var documents = await db.PurchaseDocuments.AsNoTracking().Where(x => x.PurchaseId == purchaseId).OrderBy(x => x.CreatedAt).ToListAsync();
            Assert.Equal(3, documents.Count);
            Assert.All(documents, document =>
            {
                Assert.Equal("receipt", document.DocumentType);
                Assert.Equal("image/png", document.MediaType);
                Assert.Equal("uploaded", document.Status);
                Assert.Equal(64, document.Sha256.Length);
                Assert.Equal(1, document.PageCount);
            });

            var sourceCount = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM "ReceiptScanSources" WHERE "ReceiptScanJobId" = {jobId}
                """).SingleAsync();
            Assert.Equal(3, sourceCount);

            var rows = await db.Database.SqlQuery<ReceiptScanJobRow>($"""
                SELECT "Id", "FullWorthSpaceId", "UserId", "PurchaseId", "FileName", "ContentType", "Status", "Stage",
                       "Engine", "Error", "WarningsJson", "Attempts", "CreatedAt", "StartedAt", "CompletedAt", "UpdatedAt"
                FROM "ReceiptScanJobs" WHERE "Id" = {jobId}
                """).ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(ReceiptScanJobStatuses.Draft, row.Status);
        });

        using var sourcesRequest = UserRequest(HttpMethod.Get, $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources?fullWorthSpaceId={spaceId:D}", userId);
        using var sourcesResponse = await client.SendAsync(sourcesRequest);
        Assert.Equal(HttpStatusCode.OK, sourcesResponse.StatusCode);
        using var sourcesJson = JsonDocument.Parse(await sourcesResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, sourcesJson.RootElement.GetArrayLength());
        Assert.Equal("top.png", sourcesJson.RootElement[0].GetProperty("originalFileName").GetString());
        Assert.Equal("middle.png", sourcesJson.RootElement[1].GetProperty("originalFileName").GetString());
        Assert.Equal("bottom.png", sourcesJson.RootElement[2].GetProperty("originalFileName").GetString());

        using var start = UserRequest(HttpMethod.Post, $"/api/purchases/receipt-scan/jobs/{jobId:D}/start?fullWorthSpaceId={spaceId:D}", userId);
        using var startResponse = await client.SendAsync(start);
        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
        using var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        Assert.Equal("queued", startJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, startJson.RootElement.GetProperty("sourceCount").GetInt32());

        using var lateAdd = AddSourcesRequest(spaceId, userId, jobId, ("late.png", ImageBytes(4), Guid.NewGuid()));
        using var lateAddResponse = await client.SendAsync(lateAdd);
        Assert.Equal(HttpStatusCode.BadRequest, lateAddResponse.StatusCode);
    }

    [Fact]
    public async Task DraftSourcesCanBeAddedReorderedRemovedAndReplacedWithoutRecreatingPurchase()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        using var create = QueueRequest(spaceId, userId, jobId,
            ("first.png", ImageBytes(10), firstId),
            ("second.png", ImageBytes(11), secondId));
        using var createResponse = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        using var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var purchaseId = createJson.RootElement.GetProperty("purchaseId").GetGuid();

        using var reorder = UserRequest(HttpMethod.Put, $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources/order?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent(new { sourceIds = new[] { secondId, firstId } }));
        using var reorderResponse = await client.SendAsync(reorder);
        Assert.Equal(HttpStatusCode.OK, reorderResponse.StatusCode);
        using var reorderJson = JsonDocument.Parse(await reorderResponse.Content.ReadAsStringAsync());
        Assert.Equal(secondId, reorderJson.RootElement[0].GetProperty("id").GetGuid());

        var thirdId = Guid.NewGuid();
        using var add = AddSourcesRequest(spaceId, userId, jobId, ("third.png", ImageBytes(12), thirdId));
        using var addResponse = await client.SendAsync(add);
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        using var addJson = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, addJson.RootElement.GetArrayLength());

        using var remove = UserRequest(HttpMethod.Delete, $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources/{firstId:D}?fullWorthSpaceId={spaceId:D}", userId);
        using var removeResponse = await client.SendAsync(remove);
        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
        using var removeJson = JsonDocument.Parse(await removeResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, removeJson.RootElement.GetArrayLength());

        var replacement = new MultipartFormDataContent();
        replacement.Add(new ByteArrayContent(ImageBytes(13)), "receipt", "replacement.png");
        using var replace = UserRequest(HttpMethod.Put, $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources/{secondId:D}?fullWorthSpaceId={spaceId:D}", userId, replacement);
        using var replaceResponse = await client.SendAsync(replace);
        Assert.Equal(HttpStatusCode.OK, replaceResponse.StatusCode);
        using var replaceJson = JsonDocument.Parse(await replaceResponse.Content.ReadAsStringAsync());
        Assert.Equal(secondId, replaceJson.RootElement[0].GetProperty("id").GetGuid());
        Assert.Equal("replacement.png", replaceJson.RootElement[0].GetProperty("originalFileName").GetString());

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.Id == purchaseId));
            Assert.Equal(2, await db.PurchaseDocuments.CountAsync(x => x.PurchaseId == purchaseId));
            var sourceCount = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM "ReceiptScanSources" WHERE "ReceiptScanJobId" = {jobId}
                """).SingleAsync();
            Assert.Equal(2, sourceCount);
        });
    }

    [Fact]
    public async Task SameReceiptContentWithNewJobIdIsRejectedWithoutDuplicatePurchase()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        using var first = QueueRequest(spaceId, userId, Guid.NewGuid(), ("first.png", ImageBytes(21), Guid.NewGuid()));
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);

        using var duplicate = QueueRequest(spaceId, userId, Guid.NewGuid(), ("same-content.png", ImageBytes(21), Guid.NewGuid()));
        using var duplicateResponse = await client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);
        Assert.Contains("already stored", await duplicateResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId));
            Assert.Equal(1, await db.PurchaseDocuments.CountAsync(x => x.Purchase.FullWorthSpaceId == spaceId));
        });
    }

    [Fact]
    public async Task QueueIsScopedToFullWorthSpaceMembership()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var memberId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = memberId, EmailNormalized = $"{memberId:N}@EXAMPLE.COM", DisplayName = "Queue member", IsActive = true },
                new FullWorthUser { Id = outsiderId, EmailNormalized = $"{outsiderId:N}@EXAMPLE.COM", DisplayName = "Queue outsider", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Queue Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = memberId, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();
        });

        using var deniedUpload = QueueRequest(spaceId, outsiderId, Guid.NewGuid(), ("denied.png", ImageBytes(30), Guid.NewGuid()));
        using var deniedUploadResponse = await client.SendAsync(deniedUpload);
        Assert.Equal(HttpStatusCode.NotFound, deniedUploadResponse.StatusCode);

        using var deniedList = UserRequest(HttpMethod.Get, $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}", outsiderId);
        using var deniedListResponse = await client.SendAsync(deniedList);
        Assert.Equal(HttpStatusCode.NotFound, deniedListResponse.StatusCode);
    }

    private static async Task SeedMemberAsync(BackendWebApplicationFactory factory, Guid userId, Guid spaceId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Queue member", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Queue Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member resolves to the read-only viewer template,
            // so grant editor (carrying purchases.manage) to reach the receipt-scan queue write handlers.
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
    }

    private static HttpRequestMessage QueueRequest(Guid fullWorthSpaceId, Guid userId, Guid clientJobId, params (string FileName, byte[] Bytes, Guid SourceId)[] files)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new StringContent(clientJobId.ToString("D")), "clientJobId");
        foreach (var file in files)
        {
            multipart.Add(new ByteArrayContent(file.Bytes), "receipt", file.FileName);
            multipart.Add(new StringContent(file.SourceId.ToString("D")), "sourceId");
        }
        return UserRequest(HttpMethod.Post, $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={fullWorthSpaceId:D}", userId, multipart);
    }

    private static HttpRequestMessage AddSourcesRequest(Guid fullWorthSpaceId, Guid userId, Guid jobId, params (string FileName, byte[] Bytes, Guid SourceId)[] files)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var file in files)
        {
            multipart.Add(new ByteArrayContent(file.Bytes), "receipt", file.FileName);
            multipart.Add(new StringContent(file.SourceId.ToString("D")), "sourceId");
        }
        return UserRequest(HttpMethod.Post, $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources?fullWorthSpaceId={fullWorthSpaceId:D}", userId, multipart);
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static byte[] ImageBytes(byte marker) =>
    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker, 0x00, 0x01, 0x02, 0x03];

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
