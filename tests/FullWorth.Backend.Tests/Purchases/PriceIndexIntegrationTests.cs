using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PriceIndexIntegrationTests
{
    [Fact]
    public async Task The_price_index_is_built_from_confirmed_receipts_without_products_and_splits_basic_needs()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var groceries = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Index user", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Index Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Categories.Add(new FinanceCategory { Id = groceries, FullWorthSpaceId = spaceId, Key = "food.groceries", Name = "Lebensmittel", IsSystem = true });

            // Wie in Produktion: erst als "needs_review" anlegen, dann bestaetigen (der Trigger oeffnet sonst die Pruefung neu).
            var purchases = new[]
            {
                Purchase(spaceId, userId, groceries, new DateOnly(2024, 2, 10), 1m),
                Purchase(spaceId, userId, groceries, new DateOnly(2024, 5, 10), 1.10m),
                Purchase(spaceId, userId, groceries, new DateOnly(2024, 5, 12), 50m) // nie bestaetigt
            };
            db.Purchases.AddRange(purchases);
            await db.SaveChangesAsync();
            foreach (var confirmed in purchases.Take(2)) { confirmed.Status = "confirmed"; confirmed.ReviewState = "confirmed"; }
            await db.SaveChangesAsync();
        });

        using var response = await client.SendAsync(UserRequest(
            $"/api/purchase-analytics/price-index?fullWorthSpaceId={spaceId:D}&from=2024-01-01&to=2024-12-31", userId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var overall = json.RootElement.GetProperty("overall");
        Assert.Equal(2, overall.GetArrayLength());
        Assert.Equal(110m, overall[1].GetProperty("index").GetDecimal());
        var food = json.RootElement.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("key").GetString() == "food");
        Assert.Equal(110m, food.GetProperty("points")[1].GetProperty("index").GetDecimal());
        Assert.Equal(0, json.RootElement.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("key").GetString() == "fuel").GetProperty("points").GetArrayLength());
    }

    private static Purchase Purchase(Guid spaceId, Guid userId, Guid category, DateOnly date, decimal factor)
    {
        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId, Source = "receipt", Merchant = "REWE Markt GmbH", PurchaseDate = date,
            TotalAmount = 0m, Currency = "EUR", Status = "review", ReviewState = "needs_review", Visibility = "space", CreatedByUserId = userId
        };
        foreach (var (name, price) in new[] { ("Milch", 1m), ("Butter", 2m), ("Nudeln", 1.5m), ("Brot", 3m) })
            purchase.Items.Add(new PurchaseItem
            {
                RawName = name, Name = name, Quantity = 1m, QuantityUnit = "piece", UnitPrice = price * factor, TotalPrice = price * factor,
                Currency = "EUR", LineType = "product", CategorizationSource = "none", CategoryId = category
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
