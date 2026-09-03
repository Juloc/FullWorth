using System.Net;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ReceiptScanQueueCleanupIntegrationTests
{
    [Fact]
    public async Task Duplicate_file_inside_new_scan_set_leaves_no_orphan_files_or_rows()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId, spaceId);

        var bytes = ImageBytes(71);
        using var request = QueueRequest(
            spaceId,
            userId,
            Guid.NewGuid(),
            ("first.png", bytes, Guid.NewGuid()),
            ("same-again.png", bytes, Guid.NewGuid()));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("same receipt file", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var physicalFiles = Directory.Exists(factory.PurchaseStorageRoot)
            ? Directory.EnumerateFiles(factory.PurchaseStorageRoot, "*", SearchOption.AllDirectories).ToList()
            : [];
        Assert.Empty(physicalFiles);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId));
            Assert.Equal(0, await db.PurchaseDocuments.CountAsync(x => x.Purchase.FullWorthSpaceId == spaceId));
            var jobs = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM "ReceiptScanJobs"
                WHERE "FullWorthSpaceId" = {spaceId}
                """).SingleAsync();
            Assert.Equal(0, jobs);
            var sources = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM "ReceiptScanSources" s
                JOIN "ReceiptScanJobs" j ON j."Id" = s."ReceiptScanJobId"
                WHERE j."FullWorthSpaceId" = {spaceId}
                """).SingleAsync();
            Assert.Equal(0, sources);
        });
    }

    private static async Task SeedMemberAsync(BackendWebApplicationFactory factory, Guid userId, Guid spaceId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Receipt cleanup",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Receipt cleanup", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member resolves to the read-only viewer template,
            // so grant editor to reach the receipt-scan queue handler (which returns the expected 400).
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
    }

    private static HttpRequestMessage QueueRequest(
        Guid fullWorthSpaceId,
        Guid userId,
        Guid clientJobId,
        params (string FileName, byte[] Bytes, Guid SourceId)[] files)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new StringContent(clientJobId.ToString("D")), "clientJobId");
        foreach (var file in files)
        {
            multipart.Add(new ByteArrayContent(file.Bytes), "receipt", file.FileName);
            multipart.Add(new StringContent(file.SourceId.ToString("D")), "sourceId");
        }
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={fullWorthSpaceId:D}")
        {
            Content = multipart
        };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static byte[] ImageBytes(byte marker) =>
    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker, 0x00, 0x01, 0x02, 0x03];
}
