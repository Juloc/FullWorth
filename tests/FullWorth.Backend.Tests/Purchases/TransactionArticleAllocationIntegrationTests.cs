using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class TransactionArticleAllocationIntegrationTests
{
    [Fact]
    public async Task MixedArticleAndCategorySplit_IsAcceptedAndRoundTripsArticleIdentity()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/transactions/{scenario.TransactionId:D}/allocations?fullWorthSpaceId={scenario.SpaceId:D}",
            scenario.OwnerId,
            new[]
            {
                new { categoryId = scenario.ArticleCategoryId, amount = -6m, note = (string?)null, purchaseItemId = (Guid?)scenario.LinkedItemId },
                new { categoryId = scenario.RemainderCategoryId, amount = -4m, note = "Rest", purchaseItemId = (Guid?)null }
            }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var read = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/transactions/{scenario.TransactionId:D}/allocations?fullWorthSpaceId={scenario.SpaceId:D}", scenario.OwnerId));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var lines = json.RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        var article = Assert.Single(lines.Where(x => x.GetProperty("purchaseItemId").ValueKind != JsonValueKind.Null));
        Assert.Equal(scenario.LinkedItemId, article.GetProperty("purchaseItemId").GetGuid());
        Assert.Equal("Milk", article.GetProperty("articleName").GetString());
        Assert.Equal(scenario.ArticleCategoryId, article.GetProperty("categoryId").GetGuid());
        Assert.Equal(-6m, article.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task PaymentLinkOnlyPurchase_IsVisibleInTransactionDetail()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/transactions/{scenario.TransactionId:D}?fullWorthSpaceId={scenario.SpaceId:D}", scenario.OwnerId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(scenario.LinkedPurchaseId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scenario.LinkedItemId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArticleFromUnrelatedPurchase_IsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/transactions/{scenario.TransactionId:D}/allocations?fullWorthSpaceId={scenario.SpaceId:D}",
            scenario.OwnerId,
            new[] { new { categoryId = scenario.ArticleCategoryId, amount = -10m, note = (string?)null, purchaseItemId = scenario.UnrelatedItemId } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("visible purchase linked to this transaction", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArticleCategoryCannotBeOverriddenByAllocation()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/transactions/{scenario.TransactionId:D}/allocations?fullWorthSpaceId={scenario.SpaceId:D}",
            scenario.OwnerId,
            new[] { new { categoryId = scenario.RemainderCategoryId, amount = -10m, note = (string?)null, purchaseItemId = scenario.LinkedItemId } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PrivateArticleOwnedByAnotherMember_IsRejectedEvenWhenItsPurchaseLinksTheTransaction()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/transactions/{scenario.TransactionId:D}/allocations?fullWorthSpaceId={scenario.SpaceId:D}",
            scenario.OwnerId,
            new[] { new { categoryId = scenario.ArticleCategoryId, amount = -10m, note = (string?)null, purchaseItemId = scenario.PrivateItemId } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ArticleAllocationSurvivesItemDeletionAsGenericCategorySplit()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        await factory.SeedAsync(async db =>
        {
            db.TransactionAllocations.Add(new TransactionAllocation
            {
                TransactionId = scenario.TransactionId,
                CategoryId = scenario.ArticleCategoryId,
                Amount = -10m,
                Note = "Milk",
                PurchaseItemId = scenario.LinkedItemId
            });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var delete = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/purchases/{scenario.LinkedPurchaseId:D}/items/{scenario.LinkedItemId:D}?fullWorthSpaceId={scenario.SpaceId:D}", scenario.OwnerId));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var allocation = await db.TransactionAllocations.AsNoTracking().SingleAsync(x => x.TransactionId == scenario.TransactionId);
            Assert.Null(allocation.PurchaseItemId);
            Assert.Equal(scenario.ArticleCategoryId, allocation.CategoryId);
            Assert.Equal(-10m, allocation.Amount);
        });
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(User(scenario.OwnerId), User(scenario.OtherMemberId));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.SpaceId, Name = "Article Split", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(Member(scenario.SpaceId, scenario.OwnerId), Member(scenario.SpaceId, scenario.OtherMemberId));
            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.ConnectionId,
                FullWorthSpaceId = scenario.SpaceId,
                Provider = "test",
                InstitutionName = "Article Bank",
                Country = "DE",
                ProviderSessionId = $"article-{scenario.ConnectionId:N}",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.AccountId,
                FullWorthSpaceId = scenario.SpaceId,
                BankConnectionId = scenario.ConnectionId,
                Provider = "test",
                IdentificationHash = $"article-{scenario.AccountId:N}",
                ProviderAccountId = $"provider-{scenario.AccountId:N}",
                InstitutionName = "Article Bank",
                DisplayName = "Card",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = scenario.AccountId, UserId = scenario.OwnerId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = scenario.TransactionId,
                AccountId = scenario.AccountId,
                ExternalKey = $"article-{scenario.TransactionId:N}",
                Amount = -10m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, 8, 30),
                Counterparty = "Market",
                RawJson = "{}"
            });
            db.Categories.AddRange(
                new FinanceCategory { Id = scenario.ArticleCategoryId, FullWorthSpaceId = scenario.SpaceId, Key = $"food-{scenario.ArticleCategoryId:N}", Name = "Food" },
                new FinanceCategory { Id = scenario.RemainderCategoryId, FullWorthSpaceId = scenario.SpaceId, Key = $"other-{scenario.RemainderCategoryId:N}", Name = "Other" });

            db.Purchases.AddRange(
                Purchase(scenario.LinkedPurchaseId, scenario.SpaceId, scenario.OwnerId, "space", "Market"),
                Purchase(scenario.UnrelatedPurchaseId, scenario.SpaceId, scenario.OwnerId, "space", "Other Market"),
                Purchase(scenario.PrivatePurchaseId, scenario.SpaceId, scenario.OtherMemberId, "private", "Private Market"));
            db.PurchaseItems.AddRange(
                Item(scenario.LinkedItemId, scenario.LinkedPurchaseId, scenario.ArticleCategoryId, "Milk", 6m),
                Item(scenario.UnrelatedItemId, scenario.UnrelatedPurchaseId, scenario.ArticleCategoryId, "Bread", 10m),
                Item(scenario.PrivateItemId, scenario.PrivatePurchaseId, scenario.ArticleCategoryId, "Secret", 10m));
            // Both purchases legitimately share the one -10 transaction, so their payment allocations must
            // sum to at most its amount (the payment-allocation guard rejects genuine over-allocation).
            db.PurchasePaymentLinks.AddRange(
                Payment(scenario.SpaceId, scenario.LinkedPurchaseId, scenario.TransactionId, 5m),
                Payment(scenario.SpaceId, scenario.PrivatePurchaseId, scenario.TransactionId, 5m));
            await db.SaveChangesAsync();

            // The acting members reach the allocation routes through the capability layer; account-level
            // ownership (only OwnerId owns the account) still gates who may mutate what.
            await CapabilityTestSeeding.GrantEditorAsync(db, scenario.SpaceId, scenario.OwnerId, scenario.OtherMemberId);
        });
        return scenario;
    }

    private static FullWorthUser User(Guid id) => new()
    {
        Id = id,
        EmailNormalized = $"{id:N}@EXAMPLE.COM".ToUpperInvariant(),
        DisplayName = $"Article {id:N}",
        IsActive = true
    };

    private static FullWorthSpaceMember Member(Guid spaceId, Guid userId) => new()
    {
        FullWorthSpaceId = spaceId,
        UserId = userId,
        Role = FullWorthSpaceRoles.Member
    };

    private static Purchase Purchase(Guid id, Guid spaceId, Guid creator, string visibility, string merchant) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Source = "receipt",
        Merchant = merchant,
        PurchaseDate = new DateOnly(2026, 8, 30),
        TotalAmount = 10m,
        Currency = "EUR",
        Status = "review",
        ReviewState = "needs_review",
        CreatedByUserId = creator,
        Visibility = visibility
    };

    private static PurchaseItem Item(Guid id, Guid purchaseId, Guid categoryId, string name, decimal total) => new()
    {
        Id = id,
        PurchaseId = purchaseId,
        CategoryId = categoryId,
        RawName = name,
        Name = name,
        Quantity = 1m,
        QuantityUnit = "piece",
        UnitPrice = total,
        TotalPrice = total,
        Currency = "EUR",
        LineType = "product",
        CategorizationSource = "manual"
    };

    private static PurchasePaymentLink Payment(Guid spaceId, Guid purchaseId, Guid transactionId, decimal amount) => new()
    {
        FullWorthSpaceId = spaceId,
        PurchaseId = purchaseId,
        TransactionId = transactionId,
        Amount = amount,
        Currency = "EUR",
        LinkSource = "manual"
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scenario(
        Guid OwnerId,
        Guid OtherMemberId,
        Guid SpaceId,
        Guid ConnectionId,
        Guid AccountId,
        Guid TransactionId,
        Guid ArticleCategoryId,
        Guid RemainderCategoryId,
        Guid LinkedPurchaseId,
        Guid LinkedItemId,
        Guid UnrelatedPurchaseId,
        Guid UnrelatedItemId,
        Guid PrivatePurchaseId,
        Guid PrivateItemId,
        Guid SpareId);
}
