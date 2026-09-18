using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// HTTP-level coverage for POST /api/purchases/receipt-imports/batches/{id}/rollback (#141): the happy
/// path, refusing a second rollback, and a batch where only some of its purchases are still eligible.
/// </summary>
public sealed class ReceiptImportRollbackEndpointTests
{
    [Fact]
    public async Task RollbackRemovesTheImportedPurchasesAndCanOnlyRunOnce()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        await Seed(factory, owner, batchId, [purchaseId]);

        var first = await Rollback(client, owner, batchId);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(1, first.Body!.Value.GetProperty("removed").GetInt32());
        Assert.Equal(0, first.Body!.Value.GetProperty("kept").GetInt32());
        await factory.SeedAsync(async db => Assert.False(await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == purchaseId)));

        var second = await Rollback(client, owner, batchId);
        Assert.Equal(HttpStatusCode.BadRequest, second.Status);
    }

    [Fact]
    public async Task ADependencyOnOnePurchaseKeepsOnlyThatOne()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var (keptId, removedId) = (Guid.NewGuid(), Guid.NewGuid());
        await Seed(factory, owner, batchId, [keptId, removedId]);

        await factory.SeedAsync(async db =>
        {
            db.Set<PurchaseDifferenceAcceptance>().Add(new PurchaseDifferenceAcceptance
            {
                PurchaseId = keptId,
                Kind = "items",
                Amount = 1m,
                Reason = "other",
                AcceptedByUserId = owner
            });
            await db.SaveChangesAsync();
        });

        var rollback = await Rollback(client, owner, batchId);
        Assert.Equal(HttpStatusCode.OK, rollback.Status);
        Assert.Equal(1, rollback.Body!.Value.GetProperty("removed").GetInt32());
        Assert.Equal(1, rollback.Body!.Value.GetProperty("kept").GetInt32());

        await factory.SeedAsync(async db =>
        {
            Assert.True(await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == keptId));
            Assert.False(await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == removedId));
        });
    }

    private static async Task<(HttpStatusCode Status, JsonElement? Body)> Rollback(HttpClient client, Guid owner, Guid batchId)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/purchases/receipt-imports/batches/{batchId:D}/rollback?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string url, Guid userId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task Seed(BackendWebApplicationFactory factory, Guid userId, Guid batchId, IReadOnlyList<Guid> purchaseIds)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Receipt rollback owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            foreach (var purchaseId in purchaseIds)
                db.Purchases.Add(new Purchase
                {
                    Id = purchaseId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Source = "receipt",
                    Merchant = "Test Shop",
                    TotalAmount = 12.34m,
                    Currency = "EUR",
                    Status = "review"
                });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ReceiptImportBatches"
                    ("Id", "FullWorthSpaceId", "UserId", "SourceType", "SourceName", "Currency", "Status", "AutoStart", "CreatedAt", "UpdatedAt")
                VALUES
                    ({batchId}, {FullWorthSpaceDefaults.LegacyId}, {userId}, {"upload"}, {"File upload"}, {"EUR"}, {ReceiptImportStatuses.Completed}, {false}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
                """);
            await PurchaseImportProvenance.LinkAsync(db, batchId, PurchaseImportSources.Receipt, purchaseIds, CancellationToken.None);
        });
    }
}
