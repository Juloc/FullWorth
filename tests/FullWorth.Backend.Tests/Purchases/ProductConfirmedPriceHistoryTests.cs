using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ProductConfirmedPriceHistoryTests
{
    [Fact]
    public async Task Draft_price_observation_never_becomes_latest_product_price()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var confirmedPurchaseId = Guid.NewGuid();
        var draftPurchaseId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Price history user",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Price history", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Products.Add(new Product
            {
                Id = productId,
                FullWorthSpaceId = spaceId,
                CanonicalName = "Test-Cola",
                DefaultQuantityUnit = "piece"
            });
            db.Purchases.AddRange(
                new Purchase
                {
                    Id = confirmedPurchaseId,
                    FullWorthSpaceId = spaceId,
                    Source = "receipt",
                    Merchant = "Supermarkt",
                    PurchaseDate = new DateOnly(2026, 8, 1),
                    TotalAmount = 2m,
                    Currency = "EUR",
                    // Seeded as needs_review so the AFTER INSERT integrity trigger on PurchaseItems (which
                    // reopens review whenever items land on a confirmed purchase) is a no-op. The purchase
                    // is promoted to confirmed below, once its items exist — mirroring production, where
                    // confirmation always follows item entry as a separate step.
                    Status = "review",
                    ReviewState = "needs_review",
                    CreatedByUserId = userId,
                    Visibility = "space"
                },
                new Purchase
                {
                    Id = draftPurchaseId,
                    FullWorthSpaceId = spaceId,
                    Source = "receipt",
                    Merchant = "OCR Entwurf",
                    PurchaseDate = new DateOnly(2026, 8, 31),
                    TotalAmount = 9m,
                    Currency = "EUR",
                    Status = "review",
                    ReviewState = "needs_review",
                    CreatedByUserId = userId,
                    Visibility = "space"
                });
            db.PurchaseItems.AddRange(
                new PurchaseItem
                {
                    PurchaseId = confirmedPurchaseId,
                    ProductId = productId,
                    RawName = "COLA",
                    Name = "Cola",
                    Quantity = 1m,
                    QuantityUnit = "piece",
                    UnitPrice = 2m,
                    OriginalUnitPrice = 2.5m,
                    DiscountAmount = .5m,
                    DiscountLabel = "Aktion",
                    TotalPrice = 2m,
                    Currency = "EUR",
                    LineType = "product"
                },
                new PurchaseItem
                {
                    PurchaseId = draftPurchaseId,
                    ProductId = productId,
                    RawName = "COLA OCR",
                    Name = "Cola",
                    Quantity = 1m,
                    QuantityUnit = "piece",
                    UnitPrice = 9m,
                    TotalPrice = 9m,
                    Currency = "EUR",
                    LineType = "product"
                });
            await db.SaveChangesAsync();

            // Promote to confirmed after the items are persisted (a Purchases-only UPDATE does not fire the
            // PurchaseItems review trigger), so the confirmed observation genuinely survives seeding.
            var confirmed = await db.Purchases.SingleAsync(x => x.Id == confirmedPurchaseId);
            confirmed.Status = "confirmed";
            confirmed.ReviewState = "confirmed";
            await db.SaveChangesAsync();
        });

        using var listRequest = UserRequest(HttpMethod.Get, $"/api/products?fullWorthSpaceId={spaceId:D}", userId);
        using var listResponse = await client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var product = Assert.Single(listJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, product.GetProperty("purchaseCount").GetInt32());
        Assert.Equal(2m, product.GetProperty("lastPrice").GetDecimal());
        Assert.Equal(2.5m, product.GetProperty("lastOriginalPrice").GetDecimal());
        Assert.Equal(.5m, product.GetProperty("lastDiscountAmount").GetDecimal());

        using var historyRequest = UserRequest(HttpMethod.Get, $"/api/products/{productId:D}/history?fullWorthSpaceId={spaceId:D}", userId);
        using var historyResponse = await client.SendAsync(historyRequest);
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var historyJson = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, historyJson.RootElement.GetProperty("count").GetInt32());
        var observation = Assert.Single(historyJson.RootElement.GetProperty("observations").EnumerateArray());
        Assert.Equal(confirmedPurchaseId, observation.GetProperty("purchaseId").GetGuid());
        Assert.Equal(2m, observation.GetProperty("effectivePrice").GetDecimal());
        Assert.Equal(20m, observation.GetProperty("savingsPercent").GetDecimal());

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(2, await db.PurchaseItems.CountAsync(x => x.ProductId == productId));
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}