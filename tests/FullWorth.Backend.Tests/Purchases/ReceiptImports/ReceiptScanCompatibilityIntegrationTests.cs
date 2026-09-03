using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

public sealed class ReceiptScanCompatibilityIntegrationTests
{
    [Fact]
    public async Task ExistingMultiPhotoScanStillCreatesOneLogicalReceiptWithOrderedSources()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var sourceIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await SeedMemberAsync(factory, userId, spaceId);

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new StringContent(jobId.ToString("D")), "clientJobId");
        multipart.Add(new StringContent(string.Join(',', sourceIds.Select(id => id.ToString("D")))), "sourceIds");
        AddPng(multipart, "top.png", 21);
        AddPng(multipart, "bottom.png", 22);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={spaceId:D}")
        {
            Content = multipart
        };
        AddHeaders(request, userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var job = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(jobId, job.RootElement.GetProperty("id").GetGuid());
        var purchaseId = job.RootElement.GetProperty("purchaseId").GetGuid();
        Assert.NotEqual(Guid.Empty, purchaseId);

        using var sourcesRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/purchases/receipt-scan/jobs/{jobId:D}/sources?fullWorthSpaceId={spaceId:D}");
        AddHeaders(sourcesRequest, userId);
        using var sourcesResponse = await client.SendAsync(sourcesRequest);

        Assert.Equal(HttpStatusCode.OK, sourcesResponse.StatusCode);
        using var sources = JsonDocument.Parse(await sourcesResponse.Content.ReadAsStringAsync());
        var rows = sources.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(sourceIds, rows.Select(row => row.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(new[] { 0, 1 }, rows.Select(row => row.GetProperty("sortOrder").GetInt32()).ToArray());
        Assert.All(rows, row => Assert.Equal("image", row.GetProperty("sourceType").GetString()));

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
            Assert.Equal(2, await db.PurchaseDocuments.CountAsync(x => x.PurchaseId == purchaseId));
        });
    }

    private static void AddPng(MultipartFormDataContent multipart, string fileName, byte marker)
    {
        var content = new ByteArrayContent([137, 80, 78, 71, 13, 10, 26, 10, marker, 0, 0, 0]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(content, "receipt", fileName);
    }

    private static void AddHeaders(HttpRequestMessage request, Guid userId)
    {
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
    }

    private static async Task SeedMemberAsync(BackendWebApplicationFactory factory, Guid userId, Guid spaceId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Receipt scan compatibility member",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = spaceId,
                Name = "Receipt Scan Compatibility Space",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();

            // LegacyParityCapabilityAuthorizationMiddleware gates every non-GET /api/purchases write with
            // purchases.manage; a plain member resolves to the read-only viewer template, so grant the
            // editor template (which carries purchases.manage).
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
    }
}
