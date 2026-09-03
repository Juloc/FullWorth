using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseDiscountFinancialIntegrationTests
{
    [Fact]
    public async Task CouponAndDepositUseReceiptEquationInsteadOfLegacyItemDifference()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedAsync(factory, userId, spaceId, purchaseId, itemId, 36.98m, 35.98m);

        using var save = UserRequest(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/financials?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new
            {
                subtotalAmount = 42.18m,
                discountAmount = 6.20m,
                depositAmount = 1.00m,
                taxAmount = 3.84m,
                roundingAmount = 0m,
                items = new[]
                {
                    new { purchaseItemId = itemId, originalUnitPrice = (decimal?)null, discountAmount = 0m, discountLabel = (string?)null, depositAmount = 1m }
                },
                discounts = new[]
                {
                    new { id = (Guid?)null, purchaseItemId = (Guid?)null, type = "coupon", label = "App-Coupon", amount = 6.20m, percentage = (decimal?)null, couponCode = "APP", rawText = "App-Coupon -6,20 €", source = "manual", confidence = (decimal?)null }
                }
            }));
        using var saveResponse = await client.SendAsync(save);
        Assert.Equal(HttpStatusCode.NoContent, saveResponse.StatusCode);

        using var reconciliation = UserRequest(HttpMethod.Get, $"/api/purchases/{purchaseId:D}/reconciliation?fullWorthSpaceId={spaceId:D}", userId);
        using var reconciliationResponse = await client.SendAsync(reconciliation);
        Assert.Equal(HttpStatusCode.OK, reconciliationResponse.StatusCode);
        using var json = JsonDocument.Parse(await reconciliationResponse.Content.ReadAsStringAsync());
        Assert.Equal("receipt_financials", json.RootElement.GetProperty("reconciliationBasis").GetString());
        Assert.Equal(36.98m, json.RootElement.GetProperty("calculatedTotal").GetDecimal());
        Assert.Equal(0m, json.RootElement.GetProperty("itemDifference").GetDecimal());
        Assert.True(json.RootElement.GetProperty("itemsReconciled").GetBoolean());
        Assert.True(json.RootElement.GetProperty("fullyReconciled").GetBoolean());
    }

    [Fact]
    public async Task LegacyItemEditPreservesItemFinancialsAndDiscountRelationship()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedAsync(factory, userId, spaceId, purchaseId, itemId, 8m, 8m);

        using var financialRequest = UserRequest(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/financials?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new
            {
                subtotalAmount = 10m,
                discountAmount = 2m,
                depositAmount = 0m,
                taxAmount = (decimal?)null,
                roundingAmount = 0m,
                items = new[]
                {
                    new { purchaseItemId = itemId, originalUnitPrice = 10m, discountAmount = 2m, discountLabel = "Aktion", depositAmount = 0m }
                },
                discounts = new[]
                {
                    new { id = (Guid?)null, purchaseItemId = (Guid?)itemId, type = "promotion", label = "Aktion", amount = 2m, percentage = (decimal?)null, couponCode = (string?)null, rawText = "Aktion -2,00 €", source = "manual", confidence = (decimal?)null }
                }
            }));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(financialRequest)).StatusCode);

        using var itemRequest = UserRequest(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/items?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new[]
            {
                new { categoryId = (Guid?)null, name = "Kaffee neu", brand = (string?)null, sku = (string?)null, asin = (string?)null, quantity = 1m, unitPrice = (decimal?)8m, totalPrice = 8m, currency = "EUR", notes = (string?)null }
            }));
        using var itemResponse = await client.SendAsync(itemRequest);
        Assert.Equal(HttpStatusCode.NoContent, itemResponse.StatusCode);

        using var get = UserRequest(HttpMethod.Get, $"/api/purchases/{purchaseId:D}/financials?fullWorthSpaceId={spaceId:D}", userId);
        using var getResponse = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var result = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        var item = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(10m, item.GetProperty("originalUnitPrice").GetDecimal());
        Assert.Equal(2m, item.GetProperty("discountAmount").GetDecimal());
        Assert.Equal("Aktion", item.GetProperty("discountLabel").GetString());
        var newItemId = item.GetProperty("purchaseItemId").GetGuid();
        Assert.NotEqual(itemId, newItemId);

        var discount = Assert.Single(result.RootElement.GetProperty("discounts").EnumerateArray());
        Assert.Equal(newItemId, discount.GetProperty("purchaseItemId").GetGuid());
        Assert.Equal("promotion", discount.GetProperty("type").GetString());
        Assert.Equal(2m, discount.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task DifferenceConfirmationSurvivesIdOnlyRewriteButInvalidatesOnSemanticChange()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedAsync(factory, userId, spaceId, purchaseId, itemId, 10m, 8m);

        using var confirm = UserRequest(HttpMethod.Post, $"/api/purchase-review/{purchaseId:D}/confirm-difference?fullWorthSpaceId={spaceId:D}", userId);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(confirm)).StatusCode);
        Assert.True(await DifferenceConfirmedAsync(client, userId, spaceId, purchaseId));

        using var identicalRewrite = UserRequest(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/items?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new[]
            {
                new { categoryId = (Guid?)null, name = "Kaffee", brand = (string?)null, sku = (string?)null, asin = (string?)null, quantity = 1m, unitPrice = (decimal?)8m, totalPrice = 8m, currency = "EUR", notes = (string?)null }
            }));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(identicalRewrite)).StatusCode);
        Assert.True(await DifferenceConfirmedAsync(client, userId, spaceId, purchaseId));

        using var semanticRewrite = UserRequest(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/items?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new[]
            {
                new { categoryId = (Guid?)null, name = "Espresso", brand = (string?)null, sku = (string?)null, asin = (string?)null, quantity = 1m, unitPrice = (decimal?)8m, totalPrice = 8m, currency = "EUR", notes = (string?)null }
            }));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(semanticRewrite)).StatusCode);
        Assert.False(await DifferenceConfirmedAsync(client, userId, spaceId, purchaseId));
    }

    [Fact]
    public async Task FinancialWriteRejectsNegativeDiscountAndForeignItem()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedAsync(factory, userId, spaceId, purchaseId, itemId, 10m, 10m);

        using var negative = UserRequest(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/financials?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new { subtotalAmount = (decimal?)null, discountAmount = -1m, depositAmount = 0m, taxAmount = (decimal?)null, roundingAmount = 0m, items = Array.Empty<object>(), discounts = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(negative)).StatusCode);

        using var foreign = UserRequest(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/financials?fullWorthSpaceId={spaceId:D}", userId,
            JsonContent.Create(new
            {
                subtotalAmount = (decimal?)null, discountAmount = 0m, depositAmount = 0m, taxAmount = (decimal?)null, roundingAmount = 0m,
                items = new[] { new { purchaseItemId = Guid.NewGuid(), originalUnitPrice = (decimal?)null, discountAmount = 0m, discountLabel = (string?)null, depositAmount = 0m } },
                discounts = Array.Empty<object>()
            }));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public async Task FinancialReadIsFullWorthSpaceScoped()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedAsync(factory, ownerId, spaceId, purchaseId, itemId, 10m, 10m);
        await factory.SeedFullWorthUserAsync(outsiderId);

        using var request = UserRequest(HttpMethod.Get, $"/api/purchases/{purchaseId:D}/financials?fullWorthSpaceId={spaceId:D}", outsiderId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<bool> DifferenceConfirmedAsync(HttpClient client, Guid userId, Guid spaceId, Guid purchaseId)
    {
        using var request = UserRequest(HttpMethod.Get, $"/api/purchase-review/{purchaseId:D}?fullWorthSpaceId={spaceId:D}", userId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("differenceConfirmed").GetBoolean();
    }

    private static async Task SeedAsync(BackendWebApplicationFactory factory, Guid userId, Guid spaceId, Guid purchaseId, Guid itemId, decimal purchaseTotal, decimal itemTotal)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Purchase owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Purchase financials", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = spaceId,
                Source = "receipt",
                Merchant = "REWE",
                PurchaseDate = new DateOnly(2026, 8, 31),
                TotalAmount = purchaseTotal,
                Currency = "EUR",
                Status = "review"
            });
            db.PurchaseItems.Add(new PurchaseItem
            {
                Id = itemId,
                PurchaseId = purchaseId,
                Name = "Kaffee",
                Quantity = 1m,
                UnitPrice = itemTotal,
                TotalPrice = itemTotal,
                Currency = "EUR"
            });
            await db.SaveChangesAsync();
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
