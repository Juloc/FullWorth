using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Migrations;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseItemPackIntegrationTests
{
    [Fact]
    public async Task Saving_an_item_moves_a_pack_from_the_name_into_its_own_fields_and_prices_the_base_unit()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedAsync(factory);
        var (purchaseId, _) = await AddItemAsync(factory, spaceId, userId, "Knoblauch", "piece", 1m);

        using var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/purchases/{purchaseId:D}/items?fullWorthSpaceId={spaceId:D}", userId, new[]
        {
            new PurchaseItemWrite(null, "Knoblauch 200 g", null, null, null, 1m, 1.00m, 1.00m, "EUR", null),
            new PurchaseItemWrite(null, "Eier aus Bodenhaltung", null, null, null, 2m, 2.40m, 4.80m, "EUR", null, PackageQuantity: 10m, PackageUnit: "piece"),
            new PurchaseItemWrite(null, "Super 95", null, null, null, 1m, null, 50m, "EUR", null)
        }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var items = await db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == purchaseId).OrderBy(x => x.SortOrder).ToListAsync();
            Assert.Equal("Knoblauch", items[0].Name);
            Assert.Equal("Knoblauch 200 g", items[0].RawName);
            Assert.Equal(200m, items[0].PackageQuantity);
            Assert.Equal("g", items[0].PackageUnit);
            Assert.Equal(5.00m, items[0].BaseUnitPrice);   // 1,00 € für 200 g = 5,00 € je kg
            Assert.Equal("Eier aus Bodenhaltung", items[1].Name);
            Assert.Equal(10m, items[1].PackageQuantity);
            Assert.Equal(0.24m, items[1].BaseUnitPrice);   // 2,40 € für 10 Stück
            Assert.Equal("Super 95", items[2].Name);
            Assert.Null(items[2].PackageQuantity);
        });
    }

    [Fact]
    public async Task The_backfill_reads_packs_from_old_quantities_and_names_and_keeps_the_original_text()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedAsync(factory);
        var (purchaseId, _) = await AddItemAsync(factory, spaceId, userId, "Butter", "250 g", 1m, 1.99m);
        await AddItemAsync(factory, spaceId, userId, "Tempeh natur 394 g", "piece", 1m, 2.99m, purchaseId);
        await AddItemAsync(factory, spaceId, userId, "Joghurt", "5x55g", 1m, 1.10m, purchaseId);
        await AddItemAsync(factory, spaceId, userId, "Eier, Bodenhaltung, 10er", "stk", 1m, 2.50m, purchaseId);
        await AddItemAsync(factory, spaceId, userId, "Super 95", "l", 40m, 80m, purchaseId);

        await factory.SeedAsync(db => db.Database.ExecuteSqlRawAsync(PurchaseItemPackBackfill.BackfillSql.Replace("{", "{{").Replace("}", "}}")));
        // Zweimal ist nicht schlimmer als einmal.
        await factory.SeedAsync(db => db.Database.ExecuteSqlRawAsync(PurchaseItemPackBackfill.BackfillSql.Replace("{", "{{").Replace("}", "}}")));

        await factory.SeedAsync(async db =>
        {
            var byName = await db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == purchaseId).ToDictionaryAsync(x => x.RawName);
            var butter = byName["Butter"];
            Assert.Equal((250m, "g", "piece"), (butter.PackageQuantity, butter.PackageUnit, butter.QuantityUnit));
            Assert.Equal(7.96m, butter.BaseUnitPrice);

            var tempeh = byName["Tempeh natur 394 g"];
            Assert.Equal("Tempeh natur", tempeh.Name);
            Assert.Equal(394m, tempeh.PackageQuantity);

            var joghurt = byName["Joghurt"];
            Assert.Equal((5m, 55m, "g"), (joghurt.PackageCount, joghurt.PackageQuantity, joghurt.PackageUnit));

            var eggs = byName["Eier, Bodenhaltung, 10er"];
            Assert.Equal("Eier, Bodenhaltung", eggs.Name);
            Assert.Equal((10m, "piece"), (eggs.PackageQuantity, eggs.PackageUnit));

            var fuel = byName["Super 95"];
            Assert.Null(fuel.PackageQuantity);
            Assert.Equal("l", fuel.QuantityUnit);
        });
    }

    [Fact]
    public async Task The_grocery_tree_adopts_what_the_user_already_made_and_adds_the_rest_once()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedAsync(factory);
        var alcohol = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            // Ein Space aus der Zeit vor dem Baum: die Standardkategorien darunter gab es noch nicht.
            db.Categories.RemoveRange(db.Categories.Where(x => x.FullWorthSpaceId == spaceId && x.Key.StartsWith("food.groceries.")));
            await db.SaveChangesAsync();
            db.Categories.Add(new FinanceCategory { Id = alcohol, FullWorthSpaceId = spaceId, Key = "alkohol", Name = "Alkohol", SortOrder = 500 });
            await db.SaveChangesAsync();
        });

        using var first = await client.SendAsync(Request(HttpMethod.Post, $"/api/categories/grocery-tree?fullWorthSpaceId={spaceId:D}", userId));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("adopted").GetInt32());
        Assert.Equal(json.RootElement.GetProperty("total").GetInt32() - 1, json.RootElement.GetProperty("created").GetInt32());

        await factory.SeedAsync(async db =>
        {
            var all = await db.Categories.AsNoTracking().Where(x => x.FullWorthSpaceId == spaceId).ToListAsync();
            var groceries = all.Single(x => x.Key == "food.groceries");
            var mine = all.Single(x => x.Id == alcohol);
            Assert.Equal("food.groceries.alcohol", mine.Key);
            Assert.Equal(groceries.Id, mine.ParentId);
            Assert.Equal(mine.Id, all.Single(x => x.Key == "food.groceries.spirits").ParentId);
            Assert.Equal(1, all.Count(x => x.Name == "Alkohol"));
        });

        using var second = await client.SendAsync(Request(HttpMethod.Post, $"/api/categories/grocery-tree?fullWorthSpaceId={spaceId:D}", userId));
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(0, secondJson.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(0, secondJson.RootElement.GetProperty("adopted").GetInt32());
    }

    [Fact]
    public async Task Items_that_only_say_groceries_are_refined_by_their_name_and_nothing_else_is_touched()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedAsync(factory);
        using var tree = await client.SendAsync(Request(HttpMethod.Post, $"/api/categories/grocery-tree?fullWorthSpaceId={spaceId:D}", userId));
        Guid groceries = default, dairy = default, household = default;
        await factory.SeedAsync(async db =>
        {
            var all = await db.Categories.AsNoTracking().Where(x => x.FullWorthSpaceId == spaceId).ToListAsync();
            groceries = all.Single(x => x.Key == "food.groceries").Id;
            dairy = all.Single(x => x.Key == "food.groceries.dairy").Id;
            household = all.Single(x => x.Key == "shopping.household").Id;
        });
        var (purchaseId, _) = await AddItemAsync(factory, spaceId, userId, "Speisequark", "piece", 1m, 0.99m, categoryId: groceries);
        await AddItemAsync(factory, spaceId, userId, "Bananen", "kg", 1m, 1.49m, purchaseId, groceries);
        await AddItemAsync(factory, spaceId, userId, "Blubb", "piece", 1m, 1m, purchaseId, groceries);
        await AddItemAsync(factory, spaceId, userId, "Mozzarella", "piece", 1m, 1m, purchaseId, household);

        using var preview = await client.SendAsync(Request(HttpMethod.Post, $"/api/purchases/items/refine-grocery-categories?fullWorthSpaceId={spaceId:D}", userId));
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(3, previewJson.RootElement.GetProperty("evaluated").GetInt32());
        Assert.Equal(2, previewJson.RootElement.GetProperty("changed").GetInt32());
        await factory.SeedAsync(async db => Assert.Equal(groceries, (await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.Name == "Speisequark")).CategoryId));

        using var applied = await client.SendAsync(Request(HttpMethod.Post, $"/api/purchases/items/refine-grocery-categories?fullWorthSpaceId={spaceId:D}&apply=true", userId));
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var items = await db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == purchaseId).ToDictionaryAsync(x => x.Name);
            Assert.Equal(dairy, items["Speisequark"].CategoryId);
            Assert.NotEqual(groceries, items["Bananen"].CategoryId);
            Assert.Equal(groceries, items["Blubb"].CategoryId);
            Assert.Equal(household, items["Mozzarella"].CategoryId);
        });
    }

    private static async Task<(Guid UserId, Guid SpaceId)> SeedAsync(BackendWebApplicationFactory factory)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Pack user", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Pack Space", BaseCurrency = "EUR", DefaultCategoryLanguage = "de" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            await db.SaveChangesAsync();
            await new FullWorthSeeder().SeedDefaultCategoriesForSpaceAsync(db, spaceId, CancellationToken.None, "de");
        });
        return (userId, spaceId);
    }

    private static async Task<(Guid PurchaseId, Guid ItemId)> AddItemAsync(
        BackendWebApplicationFactory factory, Guid spaceId, Guid userId, string name, string unit, decimal quantity,
        decimal total = 2m, Guid? purchaseId = null, Guid? categoryId = null)
    {
        var item = new PurchaseItem
        {
            RawName = name, Name = name, Quantity = quantity, QuantityUnit = unit, TotalPrice = total, UnitPrice = quantity > 0 ? total / quantity : null,
            Currency = "EUR", LineType = "product", CategorizationSource = "none", CategoryId = categoryId
        };
        Guid id = purchaseId ?? Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            if (purchaseId is null)
            {
                var purchase = new Purchase
                {
                    FullWorthSpaceId = spaceId, Source = "receipt", Merchant = "REWE", PurchaseDate = new DateOnly(2026, 1, 5), TotalAmount = total, Currency = "EUR",
                    Status = "review", ReviewState = "needs_review", Visibility = "space", CreatedByUserId = userId
                };
                purchase.Items.Add(item);
                db.Purchases.Add(purchase);
                await db.SaveChangesAsync();
                id = purchase.Id;
            }
            else
            {
                item.PurchaseId = purchaseId.Value;
                db.PurchaseItems.Add(item);
                await db.SaveChangesAsync();
            }
        });
        return (id, item.Id);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
