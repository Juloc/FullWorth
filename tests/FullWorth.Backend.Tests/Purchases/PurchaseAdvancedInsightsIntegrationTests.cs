using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseAdvancedInsightsIntegrationTests
{
    [Fact]
    public async Task PersonalInflationBasketTrendAndRestockIgnoreUnconfirmedDrafts()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var firstDate = today.AddDays(-20);
        var secondDate = today.AddDays(-10);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Insight user", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Insight Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Set<Product>().Add(new Product { Id = productId, FullWorthSpaceId = spaceId, CanonicalName = "Test Product" });

            // Seed every purchase as needs_review first so the AFTER INSERT trigger on PurchaseItems (which
            // reopens review whenever items land on a confirmed purchase) is a no-op, then promote the two
            // historical purchases to confirmed once their items exist. This mirrors production, where
            // confirmation is always a separate step after item entry.
            var firstConfirmed = PurchaseWithItem(spaceId, userId, productId, firstDate, 1m, "needs_review");
            var secondConfirmed = PurchaseWithItem(spaceId, userId, productId, secondDate, 2m, "needs_review");
            db.Purchases.AddRange(
                firstConfirmed,
                secondConfirmed,
                PurchaseWithItem(spaceId, userId, productId, today, 100m, "needs_review"));
            await db.SaveChangesAsync();

            foreach (var confirmed in new[] { firstConfirmed, secondConfirmed })
            {
                confirmed.Status = "confirmed";
                confirmed.ReviewState = "confirmed";
            }
            await db.SaveChangesAsync();
        });

        var from = firstDate.AddDays(-1).ToString("yyyy-MM-dd");
        var to = today.AddDays(1).ToString("yyyy-MM-dd");

        using var inflationResponse = await client.SendAsync(UserRequest(
            $"/api/purchase-analytics/personal-inflation?fullWorthSpaceId={spaceId:D}&from={from}&to={to}", userId));
        Assert.Equal(HttpStatusCode.OK, inflationResponse.StatusCode);
        using var inflation = JsonDocument.Parse(await inflationResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, inflation.RootElement.GetProperty("trackedProducts").GetInt32());
        Assert.Equal(100m, inflation.RootElement.GetProperty("personalInflationPercent").GetDecimal());
        var products = inflation.RootElement.GetProperty("products");
        Assert.Equal(2m, products[0].GetProperty("latestPrice").GetDecimal());

        using var basketResponse = await client.SendAsync(UserRequest(
            $"/api/purchase-analytics/basket-trend?fullWorthSpaceId={spaceId:D}&from={from}&to={to}", userId));
        Assert.Equal(HttpStatusCode.OK, basketResponse.StatusCode);
        using var basket = JsonDocument.Parse(await basketResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, basket.RootElement.GetProperty("purchaseCount").GetInt32());
        var totalConfirmedSpend = basket.RootElement.GetProperty("months").EnumerateArray().Sum(x => x.GetProperty("totalSpend").GetDecimal());
        Assert.Equal(3m, totalConfirmedSpend);

        using var restockResponse = await client.SendAsync(UserRequest(
            $"/api/purchase-analytics/restock-forecast?fullWorthSpaceId={spaceId:D}&horizonDays=90", userId));
        Assert.Equal(HttpStatusCode.OK, restockResponse.StatusCode);
        using var restock = JsonDocument.Parse(await restockResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, restock.RootElement.GetProperty("count").GetInt32());
        var forecast = restock.RootElement.GetProperty("items")[0];
        Assert.Equal(2, forecast.GetProperty("purchaseCount").GetInt32());
        Assert.Equal(today, DateOnly.Parse(forecast.GetProperty("expectedNextPurchase").GetString()!));
    }

    private static Purchase PurchaseWithItem(Guid spaceId, Guid userId, Guid productId, DateOnly date, decimal price, string reviewState)
    {
        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId,
            Source = "manual",
            Merchant = "Test Shop",
            PurchaseDate = date,
            TotalAmount = price,
            Currency = "EUR",
            Status = reviewState == "confirmed" ? "confirmed" : "review",
            ReviewState = reviewState,
            Visibility = "space",
            CreatedByUserId = userId
        };
        purchase.Items.Add(new PurchaseItem
        {
            ProductId = productId,
            RawName = "Test Product",
            Name = "Test Product",
            Quantity = 1m,
            QuantityUnit = "piece",
            UnitPrice = price,
            TotalPrice = price,
            Currency = "EUR",
            LineType = "product",
            CategorizationSource = "none"
        });
        return purchase;
    }

    private static HttpRequestMessage UserRequest(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
