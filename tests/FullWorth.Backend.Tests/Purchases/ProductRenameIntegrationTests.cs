using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ProductRenameIntegrationTests
{
    [Fact]
    public async Task A_corrected_receipt_name_becomes_a_product_remembers_the_old_text_and_fixes_the_same_lines()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedAsync(factory);
        var (firstPurchase, firstItem) = await AddReceiptAsync(factory, spaceId, userId, "Brokolo 1kg");
        var (_, secondItem) = await AddReceiptAsync(factory, spaceId, userId, "Brokolo 1kg");
        var (_, otherItem) = await AddReceiptAsync(factory, spaceId, userId, "Bananen");

        using var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/purchases/{firstPurchase:D}/items/{firstItem:D}/correct-name?fullWorthSpaceId={spaceId:D}", userId,
            new ItemNameCorrection("Brokkoli", ApplyToSimilar: true)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("updatedItems").GetInt32());
        var productId = json.RootElement.GetProperty("productId").GetGuid();

        await factory.SeedAsync(async db =>
        {
            var items = await db.PurchaseItems.AsNoTracking().ToDictionaryAsync(x => x.Id);
            Assert.Equal("Brokkoli", items[firstItem].Name);
            Assert.Equal("Brokolo 1kg", items[firstItem].RawName);
            Assert.Equal(productId, items[secondItem].ProductId);
            Assert.Equal("Bananen", items[otherItem].Name);
            Assert.Null(items[otherItem].ProductId);
            Assert.Equal("brokolo 1kg", Assert.Single(db.Set<ProductAlias>().Where(a => a.ProductId == productId)).NormalizedAlias);
        });

        // Korrigiert man noch einmal auf denselben Namen, entsteht kein zweites Produkt.
        using var again = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/purchases/{firstPurchase:D}/items/{firstItem:D}/correct-name?fullWorthSpaceId={spaceId:D}", userId, new ItemNameCorrection("brokkoli")));
        using var againJson = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal(productId, againJson.RootElement.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task Renaming_a_product_keeps_its_id_remembers_the_old_name_and_renames_its_lines()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedAsync(factory);
        var productId = Guid.NewGuid();
        var (_, item) = await AddReceiptAsync(factory, spaceId, userId, "Brokolo", productId);

        using var response = await client.SendAsync(Request(HttpMethod.Patch, $"/api/products/{productId:D}?fullWorthSpaceId={spaceId:D}", userId,
            new ProductWrite("Brokkoli", null, null, null, null, null, null, null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            Assert.Equal("Brokkoli", (await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.Id == item)).Name);
            Assert.Equal("brokolo", Assert.Single(db.Set<ProductAlias>().Where(a => a.ProductId == productId)).NormalizedAlias);
        });
    }

    [Fact]
    public async Task A_later_import_with_the_old_receipt_text_lands_on_the_corrected_product()
    {
        using var factory = new BackendWebApplicationFactory();
        var (_, spaceId) = await SeedAsync(factory);
        var productId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Set<Product>().Add(new Product { Id = productId, FullWorthSpaceId = spaceId, CanonicalName = "Brokkoli" });
            db.Set<ProductAlias>().Add(new ProductAlias { ProductId = productId, Alias = "Brokolo 1kg", NormalizedAlias = "brokolo 1kg", AliasType = "correction" });
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ProductService>();
        var read = new PurchaseItemWrite(null, "Brokolo 1kg", null, null, null, 1m, null, 1.99m, "EUR", null, RawName: "BROKOLO 1KG");
        var untouched = new PurchaseItemWrite(null, "Bananen", null, null, null, 1m, null, 1m, "EUR", null);

        var result = await service.ApplyKnownNamesAsync(spaceId, [read, untouched], CancellationToken.None);

        Assert.Equal("Brokkoli", result[0].Name);
        Assert.Equal(productId, result[0].ProductId);
        Assert.Equal("BROKOLO 1KG", result[0].RawName);
        Assert.Equal("Bananen", result[1].Name);
        Assert.Null(result[1].ProductId);
    }

    private static async Task<(Guid UserId, Guid SpaceId)> SeedAsync(BackendWebApplicationFactory factory)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Rename user", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Rename Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            await db.SaveChangesAsync();
        });
        return (userId, spaceId);
    }

    private static async Task<(Guid PurchaseId, Guid ItemId)> AddReceiptAsync(BackendWebApplicationFactory factory, Guid spaceId, Guid userId, string name, Guid? productId = null)
    {
        var purchase = new Purchase
        {
            FullWorthSpaceId = spaceId, Source = "receipt", Merchant = "REWE", PurchaseDate = new DateOnly(2026, 1, 5), TotalAmount = 2m, Currency = "EUR",
            Status = "review", ReviewState = "needs_review", Visibility = "space", CreatedByUserId = userId
        };
        var item = new PurchaseItem { RawName = name, Name = name, Quantity = 1m, QuantityUnit = "piece", TotalPrice = 2m, UnitPrice = 2m, Currency = "EUR", LineType = "product", CategorizationSource = "none", ProductId = productId };
        purchase.Items.Add(item);
        await factory.SeedAsync(async db =>
        {
            if (productId is not null)
            {
                db.Set<Product>().Add(new Product { Id = productId.Value, FullWorthSpaceId = spaceId, CanonicalName = name });
                await db.SaveChangesAsync();
            }
            db.Purchases.Add(purchase);
            await db.SaveChangesAsync();
        });
        return (purchase.Id, item.Id);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
