using System.Net;
using System.Net.Http.Json;
using System.Text;
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

public sealed class PurchaseAuthorizationIntegrationTests
{
    [Fact]
    public async Task ListIncludesUnlinkedSpacePurchaseButOnlyLinkedPurchasesFromAccessibleAccounts()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var owner = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var ownerIds = await ReadPurchaseIdsAsync(owner);
        Assert.Contains(scenario.UnlinkedPurchase, ownerIds);
        Assert.Contains(scenario.LinkedPurchaseA, ownerIds);
        Assert.DoesNotContain(scenario.LinkedPurchaseB, ownerIds);

        using var memberOnly = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberOnly));
        Assert.Equal(HttpStatusCode.OK, memberOnly.StatusCode);
        var memberIds = await ReadPurchaseIdsAsync(memberOnly);
        Assert.Contains(scenario.UnlinkedPurchase, memberIds);
        Assert.DoesNotContain(scenario.LinkedPurchaseA, memberIds);
        Assert.DoesNotContain(scenario.LinkedPurchaseB, memberIds);
    }

    [Fact]
    public async Task LinkedViewerCanReadButCannotUpdatePurchase()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/purchases/{scenario.LinkedPurchaseA}?fullWorthSpaceId={scenario.SpaceA}";

        using var read = await client.SendAsync(UserRequest(HttpMethod.Get, path, scenario.ViewerA));
        using var update = await client.SendAsync(UserRequest(HttpMethod.Put, path, scenario.ViewerA, PurchaseWritePayload(scenario.TransactionA, "Viewer edit")));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    public async Task SameSpaceMemberWithoutAccountOwnerCannotReadLinkedPurchase()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var inaccessible = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{scenario.LinkedPurchaseA}?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberOnly));
        using var missing = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{Guid.NewGuid():D}?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberOnly));

        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        Assert.Equal(missing.StatusCode, inaccessible.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await inaccessible.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CrossSpacePurchaseUuidIsDenied()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{scenario.LinkedPurchaseSpaceB}?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MatchCandidatesContainOnlyOwnerAuthorizedTransactions()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var owner = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{scenario.UnlinkedPurchase}/match-candidates?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var body = await owner.Content.ReadAsStringAsync();
        Assert.Contains(scenario.TransactionA.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scenario.TransactionB.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scenario.TransactionSpaceB.ToString(), body, StringComparison.OrdinalIgnoreCase);

        using var memberOnly = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{scenario.UnlinkedPurchase}/match-candidates?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberOnly));
        Assert.Equal(HttpStatusCode.OK, memberOnly.StatusCode);
        using var memberJson = JsonDocument.Parse(await memberOnly.Content.ReadAsStringAsync());
        Assert.Equal(0, memberJson.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task LinkRevalidatesTargetTransactionOwnershipAndSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var privateTarget = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{scenario.UnlinkedPurchase}/link?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            new { transactionId = scenario.TransactionB, confidence = 0.99m }));
        Assert.Equal(HttpStatusCode.NotFound, privateTarget.StatusCode);

        using var viewerTarget = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{scenario.UnlinkedPurchase}/link?fullWorthSpaceId={scenario.SpaceA}", scenario.ViewerA,
            new { transactionId = scenario.TransactionA, confidence = 0.99m }));
        Assert.Equal(HttpStatusCode.Forbidden, viewerTarget.StatusCode);

        using var crossSpace = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{scenario.UnlinkedPurchase}/link?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            new { transactionId = scenario.TransactionSpaceB, confidence = 0.99m }));
        Assert.Equal(HttpStatusCode.NotFound, crossSpace.StatusCode);

        using var success = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{scenario.UnlinkedPurchase}/link?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            new { transactionId = scenario.TransactionA, confidence = 0.99m }));
        Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
    }

    [Fact]
    public async Task ItemCategoryCannotCrossFullWorthSpaceBoundary()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/purchases/{scenario.UnlinkedPurchase}/items?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA,
            new[]
            {
                new
                {
                    categoryId = scenario.CategoryB,
                    name = "Foreign category item",
                    brand = (string?)null,
                    sku = (string?)null,
                    asin = (string?)null,
                    quantity = 1m,
                    unitPrice = (decimal?)10m,
                    totalPrice = 10m,
                    currency = "EUR",
                    notes = (string?)null
                }
            }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReconciliationDoesNotRevealInaccessibleLinkedTransaction()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var inaccessible = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{scenario.LinkedPurchaseB}/reconciliation?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA));
        using var missing = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{Guid.NewGuid():D}/reconciliation?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA));

        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
        Assert.Equal(missing.StatusCode, inaccessible.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await inaccessible.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PublicPurchaseDtoDoesNotExposeReceiptStoragePath()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{scenario.UnlinkedPurchase}?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("receiptImagePath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-storage-marker", body, StringComparison.Ordinal);
        Assert.Contains("hasReceipt", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReceiptEndpointRejectsTraversalAndInvisiblePurchase()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var traversal = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{scenario.UnlinkedPurchase}/receipt?fullWorthSpaceId={scenario.SpaceA}", scenario.OwnerA));
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);

        using var invisible = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/purchases/{scenario.LinkedPurchaseA}/receipt?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberOnly));
        Assert.Equal(HttpStatusCode.NotFound, invisible.StatusCode);
    }

    [Fact]
    public async Task AmazonImportUsesSelectedFullWorthSpaceAndRequiresMembership()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var orderId = $"ORDER-{Guid.NewGuid():N}";
        var payload = new
        {
            orderId,
            purchaseDate = "2026-08-15",
            totalAmount = 17.50m,
            currency = "EUR",
            sourceReference = "amazon-test",
            items = new[]
            {
                new
                {
                    categoryId = (Guid?)null,
                    name = "Imported item",
                    brand = (string?)null,
                    sku = (string?)null,
                    asin = "B000TEST",
                    quantity = 1m,
                    unitPrice = (decimal?)17.50m,
                    totalPrice = 17.50m
                }
            }
        };

        using var success = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/import/amazon?fullWorthSpaceId={scenario.SpaceA}", scenario.MemberOnly, payload));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var imported = await db.Purchases.AsNoTracking().SingleAsync(x => x.Source == "amazon" && x.ExternalOrderId == orderId);
            Assert.Equal(scenario.SpaceA, imported.FullWorthSpaceId);
        });

        using var denied = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/import/amazon?fullWorthSpaceId={scenario.SpaceA}", scenario.OutsideUser,
            new
            {
                orderId = $"DENIED-{Guid.NewGuid():N}",
                purchaseDate = "2026-08-15",
                totalAmount = 1m,
                currency = "EUR",
                sourceReference = (string?)null,
                items = Array.Empty<object>()
            }));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private static object PurchaseWritePayload(Guid? transactionId, string merchant) => new
    {
        transactionId,
        source = "receipt",
        merchant,
        externalOrderId = (string?)null,
        purchaseDate = "2026-08-15",
        totalAmount = 12.34m,
        currency = "EUR",
        status = "review",
        sourceReference = (string?)null,
        notes = (string?)null
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<HashSet<Guid>> ReadPurchaseIdsAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.OwnerA, scenario.OwnerB, scenario.ViewerA, scenario.MemberOnly, scenario.OutsideUser })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"Purchase user {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = scenario.SpaceA, Name = "Purchase Space A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.SpaceB, Name = "Purchase Space B", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                Member(scenario.SpaceA, scenario.OwnerA),
                Member(scenario.SpaceA, scenario.OwnerB),
                Member(scenario.SpaceA, scenario.ViewerA),
                Member(scenario.SpaceA, scenario.MemberOnly),
                Member(scenario.SpaceB, scenario.OwnerA),
                Member(scenario.SpaceB, scenario.OutsideUser));

            db.BankConnections.AddRange(
                Connection(scenario.ConnectionA, scenario.SpaceA),
                Connection(scenario.ConnectionB, scenario.SpaceB));
            db.Accounts.AddRange(
                Account(scenario.AccountA, scenario.SpaceA, scenario.ConnectionA, "Purchase Account A"),
                Account(scenario.AccountB, scenario.SpaceA, scenario.ConnectionA, "Purchase Account B"),
                Account(scenario.AccountSpaceB, scenario.SpaceB, scenario.ConnectionB, "Purchase Account Space B"));
            db.AccountOwners.AddRange(
                Owner(scenario.AccountA, scenario.OwnerA, AccountOwnershipTypes.Owner),
                Owner(scenario.AccountA, scenario.ViewerA, AccountOwnershipTypes.Viewer),
                Owner(scenario.AccountB, scenario.OwnerB, AccountOwnershipTypes.Owner),
                Owner(scenario.AccountSpaceB, scenario.OwnerA, AccountOwnershipTypes.Owner));

            db.Transactions.AddRange(
                Transaction(scenario.TransactionA, scenario.AccountA, "Purchase Shop"),
                Transaction(scenario.TransactionB, scenario.AccountB, "Private Shop"),
                Transaction(scenario.TransactionSpaceB, scenario.AccountSpaceB, "Other Space Shop"));

            db.Categories.AddRange(
                new FinanceCategory { Id = scenario.CategoryA, FullWorthSpaceId = scenario.SpaceA, Key = $"p-a-{scenario.CategoryA:N}", Name = "Purchase A" },
                new FinanceCategory { Id = scenario.CategoryB, FullWorthSpaceId = scenario.SpaceB, Key = $"p-b-{scenario.CategoryB:N}", Name = "Purchase B" });

            db.Purchases.AddRange(
                new Purchase
                {
                    Id = scenario.UnlinkedPurchase,
                    FullWorthSpaceId = scenario.SpaceA,
                    Source = "receipt",
                    Merchant = "Purchase Shop",
                    PurchaseDate = new DateOnly(2026, 8, 15),
                    TotalAmount = 12.34m,
                    Currency = "EUR",
                    ReceiptImagePath = "../private-storage-marker.pdf"
                },
                new Purchase
                {
                    Id = scenario.LinkedPurchaseA,
                    FullWorthSpaceId = scenario.SpaceA,
                    TransactionId = scenario.TransactionA,
                    Source = "receipt",
                    Merchant = "Purchase Shop",
                    PurchaseDate = new DateOnly(2026, 8, 15),
                    TotalAmount = 12.34m,
                    Currency = "EUR",
                    ReceiptImagePath = "2026/08/private-owner-a.pdf"
                },
                new Purchase
                {
                    Id = scenario.LinkedPurchaseB,
                    FullWorthSpaceId = scenario.SpaceA,
                    TransactionId = scenario.TransactionB,
                    Source = "receipt",
                    Merchant = "Private Shop",
                    PurchaseDate = new DateOnly(2026, 8, 15),
                    TotalAmount = 12.34m,
                    Currency = "EUR"
                },
                new Purchase
                {
                    Id = scenario.LinkedPurchaseSpaceB,
                    FullWorthSpaceId = scenario.SpaceB,
                    TransactionId = scenario.TransactionSpaceB,
                    Source = "receipt",
                    Merchant = "Other Space Shop",
                    PurchaseDate = new DateOnly(2026, 8, 15),
                    TotalAmount = 12.34m,
                    Currency = "EUR"
                });

            await db.SaveChangesAsync();

            // Predates the capability layer: these members own accounts and expect account-ownership to
            // govern purchase mutations, but each resolves to the read-only viewer template. Grant editor
            // to every member; the endpoint's account-level write checks (viewer/no-owner => 403/404 from
            // PurchaseAuthorizationStore) remain the effective boundary for the negative assertions.
            await CapabilityTestSeeding.GrantEditorAsync(db, scenario.SpaceA,
                scenario.OwnerA, scenario.OwnerB, scenario.ViewerA, scenario.MemberOnly);
            await CapabilityTestSeeding.GrantEditorAsync(db, scenario.SpaceB, scenario.OwnerA, scenario.OutsideUser);
        });

        return scenario;
    }

    private static FullWorthSpaceMember Member(Guid spaceId, Guid userId) => new()
    {
        FullWorthSpaceId = spaceId,
        UserId = userId,
        Role = FullWorthSpaceRoles.Member
    };

    private static BankConnection Connection(Guid id, Guid spaceId) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Provider = "test",
        InstitutionName = "Purchase Test Bank",
        Country = "DE",
        ProviderSessionId = $"purchase-{id:N}",
        Status = "AUTHORIZED"
    };

    private static FinanceAccount Account(Guid id, Guid spaceId, Guid connectionId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        BankConnectionId = connectionId,
        Provider = "test",
        IdentificationHash = $"purchase-{id:N}",
        ProviderAccountId = $"provider-{id:N}",
        InstitutionName = "Purchase Test Bank",
        DisplayName = name,
        Currency = "EUR"
    };

    private static AccountOwner Owner(Guid accountId, Guid userId, string ownershipType) => new()
    {
        AccountId = accountId,
        UserId = userId,
        OwnershipType = ownershipType
    };

    private static FinanceTransaction Transaction(Guid id, Guid accountId, string counterparty) => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = $"purchase-{id:N}",
        Amount = -12.34m,
        Currency = "EUR",
        BookingDate = new DateOnly(2026, 8, 15),
        Counterparty = counterparty,
        RawJson = "{}"
    };

    private sealed record Scenario(
        Guid OwnerA,
        Guid OwnerB,
        Guid ViewerA,
        Guid MemberOnly,
        Guid OutsideUser,
        Guid SpaceA,
        Guid SpaceB,
        Guid ConnectionA,
        Guid ConnectionB,
        Guid AccountA,
        Guid AccountB,
        Guid AccountSpaceB,
        Guid TransactionA,
        Guid TransactionB,
        Guid TransactionSpaceB,
        Guid CategoryA,
        Guid CategoryB,
        Guid UnlinkedPurchase,
        Guid LinkedPurchaseA,
        Guid LinkedPurchaseB,
        Guid LinkedPurchaseSpaceB);
}
