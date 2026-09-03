using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseDiscountAnalyticsIntegrationTests
{
    [Fact]
    public async Task AnalyticsConvertsToSpaceBaseCurrencyAndMarksMissingFxIncomplete()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var eurPurchase = Guid.NewGuid();
        var usdPurchase = Guid.NewGuid();
        var gbpPurchase = Guid.NewGuid();
        var date = new DateOnly(2026, 8, 31);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Analytics owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Discount analytics", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            db.Purchases.AddRange(
                Purchase(eurPurchase, spaceId, date, "EUR", 100m, "REWE"),
                Purchase(usdPurchase, spaceId, date, "USD", 120m, "Amazon US"),
                Purchase(gbpPurchase, spaceId, date, "GBP", 50m, "UK Shop"));
            db.FxRates.Add(new FxRate { Date = date, Currency = "USD", Rate = 1.2m });
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(FinancialWrite(userId, spaceId, eurPurchase, 10m))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(FinancialWrite(userId, spaceId, usdPurchase, 12m))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(FinancialWrite(userId, spaceId, gbpPurchase, 5m))).StatusCode);

        using var request = UserRequest(HttpMethod.Get,
            $"/api/purchases/discount-analytics?fullWorthSpaceId={spaceId:D}&from=2026-08-31&to=2026-08-31", userId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("EUR", json.RootElement.GetProperty("baseCurrency").GetString());
        Assert.True(json.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(3, json.RootElement.GetProperty("purchaseCount").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("purchasesWithDiscount").GetInt32());
        // 10 EUR + 12 USD / 1.2 = 20 EUR. GBP has no rate and must be omitted, never guessed 1:1.
        Assert.Equal(20m, json.RootElement.GetProperty("totalDiscountAmount").GetDecimal());
    }

    private static Purchase Purchase(Guid id, Guid spaceId, DateOnly date, string currency, decimal total, string merchant) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Source = "receipt",
        Merchant = merchant,
        PurchaseDate = date,
        TotalAmount = total,
        Currency = currency,
        Status = "review"
    };

    private static HttpRequestMessage FinancialWrite(Guid userId, Guid spaceId, Guid purchaseId, decimal discount) =>
        UserRequest(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/financials?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new
            {
                subtotalAmount = (decimal?)null,
                discountAmount = discount,
                depositAmount = 0m,
                taxAmount = (decimal?)null,
                roundingAmount = 0m,
                items = Array.Empty<object>(),
                discounts = Array.Empty<object>()
            }));

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
