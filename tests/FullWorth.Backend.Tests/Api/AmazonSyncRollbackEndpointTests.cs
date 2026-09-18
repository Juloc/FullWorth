using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// HTTP-level coverage for POST /api/purchases/amazon/sync-runs/{id}/rollback (#141): the happy path,
/// refusing a second rollback, and the case the whole feature exists to guard against - a re-sync that
/// only UPDATED a purchase must never let rolling back the later run delete the earlier run's purchase.
/// </summary>
public sealed class AmazonSyncRollbackEndpointTests
{
    [Fact]
    public async Task RollbackRemovesThePurchaseThisRunCreatedAndCanOnlyRunOnce()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var runId = await Seed(factory, owner, purchaseId, link: true);

        var first = await Rollback(client, owner, runId);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(1, first.Body!.Value.GetProperty("removed").GetInt32());
        Assert.Equal(0, first.Body!.Value.GetProperty("kept").GetInt32());
        await factory.SeedAsync(async db => Assert.False(await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == purchaseId)));

        var second = await Rollback(client, owner, runId);
        Assert.Equal(HttpStatusCode.BadRequest, second.Status);
    }

    /// <summary>
    /// Sync run 1 creates a purchase; sync run 2 later only updates it (order status changed, no new
    /// PurchaseImportLinks row - see AmazonOrderSyncService.UpsertAmazonOrderAsync). Rolling back run 2
    /// must not touch a purchase run 1, not run 2, is responsible for.
    /// </summary>
    [Fact]
    public async Task RollingBackARunThatOnlyUpdatedAPurchaseLeavesTheEarlierRunsPurchaseAlone()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var run1 = await Seed(factory, owner, purchaseId, link: true);
        var run2 = await Seed(factory, owner, purchaseId: null, link: false);

        var rollback = await Rollback(client, owner, run2);
        Assert.Equal(HttpStatusCode.BadRequest, rollback.Status);
        await factory.SeedAsync(async db => Assert.True(await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == purchaseId)));

        // The purchase is still run 1's responsibility and can still be rolled back through it.
        var rollbackRun1 = await Rollback(client, owner, run1);
        Assert.Equal(HttpStatusCode.OK, rollbackRun1.Status);
        Assert.Equal(1, rollbackRun1.Body!.Value.GetProperty("removed").GetInt32());
        await factory.SeedAsync(async db => Assert.False(await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == purchaseId)));
    }

    private static async Task<(HttpStatusCode Status, JsonElement? Body)> Rollback(HttpClient client, Guid owner, Guid runId)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/purchases/amazon/sync-runs/{runId:D}/rollback?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
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

    /// <summary>Seeds one Amazon sync run, optionally linking it to <paramref name="purchaseId"/> as its creator.</summary>
    private static async Task<Guid> Seed(BackendWebApplicationFactory factory, Guid userId, Guid? purchaseId, bool link)
    {
        var runId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == userId))
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                    DisplayName = "Amazon rollback owner",
                    IsActive = true
                });
                db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
                {
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    UserId = userId,
                    Role = FullWorthSpaceRoles.Owner
                });
            }

            if (purchaseId is { } id && !await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == id))
                db.Purchases.Add(new Purchase
                {
                    Id = id,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Source = "amazon",
                    Merchant = "Amazon",
                    ExternalOrderId = $"order-{id:N}",
                    TotalAmount = 29.99m,
                    Currency = "EUR",
                    Status = "review"
                });
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AmazonSyncRuns"
                    ("Id", "FullWorthSpaceId", "UserId", "StartedAt", "CompletedAt", "Status", "OrdersRead", "PurchasesCreated")
                VALUES
                    ({runId}, {FullWorthSpaceDefaults.LegacyId}, {userId}, {now}, {now}, {"success"}, 1, {(link ? 1 : 0)})
                """);
            if (link && purchaseId is { } linkedId)
                await PurchaseImportProvenance.LinkAsync(db, runId, PurchaseImportSources.Amazon, [linkedId], CancellationToken.None);
        });
        return runId;
    }
}
