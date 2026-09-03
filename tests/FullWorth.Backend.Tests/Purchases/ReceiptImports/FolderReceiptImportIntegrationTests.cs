using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases.ReceiptImports;

public sealed class FolderReceiptImportIntegrationTests
{
    [Fact]
    public async Task ExistingFilesDoNotConsumeNewImportBatchLimit()
    {
        var inbox = Path.Combine(Path.GetTempPath(), "fullworth-folder-import-limit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inbox);
        var oldFile = Path.Combine(inbox, "000-old.png");
        await File.WriteAllBytesAsync(oldFile, Png(1));
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddMinutes(-2));

        try
        {
            using var factory = CreateFactory(inbox, maxBatchItems: 1);
            using var client = factory.CreateClient();
            var userId = Guid.NewGuid();
            var spaceId = Guid.NewGuid();
            await SeedMemberAsync(factory, userId, spaceId);

            using (var first = ImportRequest(spaceId, userId))
            using (var response = await client.SendAsync(first))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(1, json.RootElement.GetProperty("queued").GetInt32());
            }

            var newFile = Path.Combine(inbox, "999-new.png");
            await File.WriteAllBytesAsync(newFile, Png(2));
            File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddMinutes(-1));

            using (var second = ImportRequest(spaceId, userId))
            using (var response = await client.SendAsync(second))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("queued").GetInt32());
                Assert.Equal(0, json.RootElement.GetProperty("skippedDuplicates").GetInt32());
                var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("999-new.png", item.GetProperty("displayName").GetString());
            }

            await factory.SeedAsync(async db =>
            {
                Assert.Equal(2, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
            });
        }
        finally
        {
            try { Directory.Delete(inbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FailedFolderItemWithoutQueueJobCanBeRetriedFromStoredSourceReference()
    {
        var inbox = Path.Combine(Path.GetTempPath(), "fullworth-folder-import-retry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inbox);
        var bytes = Png(7);
        var file = Path.Combine(inbox, "retry.png");
        await File.WriteAllBytesAsync(file, bytes);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        try
        {
            using var factory = CreateFactory(inbox);
            using var client = factory.CreateClient();
            var userId = Guid.NewGuid();
            var spaceId = Guid.NewGuid();
            await SeedMemberAsync(factory, userId, spaceId);

            Guid batchId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<ReceiptImportStore>();
                var batch = await store.CreateBatchAsync(
                    userId, spaceId, ReceiptImportSourceTypes.Folder, "Import folder", "USD", false, null, CancellationToken.None);
                batchId = batch.Id;
                var created = await store.CreateItemAsync(
                    batch.Id, spaceId, ReceiptImportSourceTypes.Folder, fingerprint, "retry.png", "retry.png", fingerprint, CancellationToken.None);
                await store.MarkFailedAsync(created.Item.Id, "temporary source read failure", CancellationToken.None);
            }

            using var retry = new HttpRequestMessage(HttpMethod.Post,
                $"/api/purchases/receipt-imports/batches/{batchId:D}/retry-failed?fullWorthSpaceId={spaceId:D}");
            AddHeaders(retry, userId);
            using var response = await client.SendAsync(retry);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("USD", json.RootElement.GetProperty("batch").GetProperty("currency").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("failed").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("queued").GetInt32());
            var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
            Assert.NotEqual(JsonValueKind.Null, item.GetProperty("receiptScanJobId").ValueKind);

            await factory.SeedAsync(async db =>
            {
                var purchase = await db.Purchases.SingleAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt");
                Assert.Equal("USD", purchase.Currency);
            });
        }
        finally
        {
            try { Directory.Delete(inbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FolderRetryCannotEscapeConfiguredInboxRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "fullworth-folder-import-traversal-tests", Guid.NewGuid().ToString("N"));
        var inbox = Path.Combine(parent, "inbox");
        Directory.CreateDirectory(inbox);
        var outsideBytes = Png(8);
        var outsideFile = Path.Combine(parent, "outside.png");
        await File.WriteAllBytesAsync(outsideFile, outsideBytes);
        File.SetLastWriteTimeUtc(outsideFile, DateTime.UtcNow.AddMinutes(-1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(outsideBytes)).ToLowerInvariant();

        try
        {
            using var factory = CreateFactory(inbox);
            using var client = factory.CreateClient();
            var userId = Guid.NewGuid();
            var spaceId = Guid.NewGuid();
            await SeedMemberAsync(factory, userId, spaceId);

            Guid batchId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<ReceiptImportStore>();
                var batch = await store.CreateBatchAsync(
                    userId, spaceId, ReceiptImportSourceTypes.Folder, "Import folder", "EUR", false, null, CancellationToken.None);
                batchId = batch.Id;
                var created = await store.CreateItemAsync(
                    batch.Id,
                    spaceId,
                    ReceiptImportSourceTypes.Folder,
                    fingerprint,
                    "outside.png",
                    "../outside.png",
                    fingerprint,
                    CancellationToken.None);
                await store.MarkFailedAsync(created.Item.Id, "temporary source read failure", CancellationToken.None);
            }

            using var retry = new HttpRequestMessage(HttpMethod.Post,
                $"/api/purchases/receipt-imports/batches/{batchId:D}/retry-failed?fullWorthSpaceId={spaceId:D}");
            AddHeaders(retry, userId);
            using var response = await client.SendAsync(retry);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("failed").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("queued").GetInt32());
            var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("receiptScanJobId").ValueKind);
            Assert.Contains("no longer available", item.GetProperty("error").GetString() ?? string.Empty);

            await factory.SeedAsync(async db =>
            {
                Assert.Equal(0, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == spaceId && x.Source == "receipt"));
            });
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    private static BackendWebApplicationFactory CreateFactory(string inbox, int maxBatchItems = 500) =>
        new(new Dictionary<string, string?>
        {
            ["ReceiptImports:FolderEnabled"] = "true",
            ["ReceiptImports:InboxPath"] = inbox,
            ["ReceiptImports:FolderStableAgeSeconds"] = "1",
            ["ReceiptImports:MaxBatchItems"] = maxBatchItems.ToString(),
            ["ReceiptImports:AutoStart"] = "false"
        });

    private static HttpRequestMessage ImportRequest(Guid fullWorthSpaceId, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/purchases/receipt-imports/folder/import?fullWorthSpaceId={fullWorthSpaceId:D}")
        {
            Content = JsonContent.Create(new { currency = "EUR", autoStart = false })
        };
        AddHeaders(request, userId);
        return request;
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
                DisplayName = "Folder import member",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Folder Import Space", BaseCurrency = "EUR" });
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

    private static byte[] Png(byte marker) => [137, 80, 78, 71, 13, 10, 26, 10, marker, 0, 0, 0];
}
