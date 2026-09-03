using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchasePaymentAllocationIntegrityTests
{
    [Fact]
    public async Task Add_payment_derives_transaction_currency_and_rejects_global_overallocation()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var firstPurchaseId = Guid.NewGuid();
        var secondPurchaseId = Guid.NewGuid();

        await SeedPaymentScenarioAsync(factory, userId, spaceId, accountId, transactionId, "USD", -10m,
            (firstPurchaseId, 6m, "EUR"), (secondPurchaseId, 5m, "USD"));

        using var client = factory.CreateClient();
        using var first = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/purchases/{firstPurchaseId:D}/payments?fullWorthSpaceId={spaceId:D}",
            userId,
            new { transactionId, amount = 6m, currency = "EUR", linkSource = "manual" }));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using (var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
            Assert.Equal("USD", body.RootElement.GetProperty("currency").GetString());

        using var second = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/purchases/{secondPurchaseId:D}/payments?fullWorthSpaceId={spaceId:D}",
            userId,
            new { transactionId, amount = 5m, currency = "USD", linkSource = "manual" }));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("transaction_overallocated", await second.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await factory.SeedAsync(async db =>
        {
            var links = await db.PurchasePaymentLinks.AsNoTracking().Where(x => x.TransactionId == transactionId).ToListAsync();
            Assert.Single(links);
            Assert.Equal(6m, links[0].Amount);
            Assert.Equal("USD", links[0].Currency);
        });
    }

    [Fact]
    public async Task Update_payment_rejects_overallocation_and_ignores_client_currency()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var firstPurchaseId = Guid.NewGuid();
        var secondPurchaseId = Guid.NewGuid();
        var firstLinkId = Guid.NewGuid();

        await SeedPaymentScenarioAsync(factory, userId, spaceId, accountId, transactionId, "EUR", -10m,
            (firstPurchaseId, 6m, "EUR"), (secondPurchaseId, 4m, "EUR"));
        await factory.SeedAsync(async db =>
        {
            db.PurchasePaymentLinks.AddRange(
                new PurchasePaymentLink { Id = firstLinkId, FullWorthSpaceId = spaceId, PurchaseId = firstPurchaseId, TransactionId = transactionId, Amount = 6m, Currency = "EUR", LinkSource = "manual", CreatedByUserId = userId },
                new PurchasePaymentLink { FullWorthSpaceId = spaceId, PurchaseId = secondPurchaseId, TransactionId = transactionId, Amount = 4m, Currency = "EUR", LinkSource = "manual", CreatedByUserId = userId });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var rejected = await client.SendAsync(UserRequest(
            HttpMethod.Patch,
            $"/api/purchases/{firstPurchaseId:D}/payments/{firstLinkId:D}?fullWorthSpaceId={spaceId:D}",
            userId,
            new { amount = 7m, currency = "USD" }));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using var accepted = await client.SendAsync(UserRequest(
            HttpMethod.Patch,
            $"/api/purchases/{firstPurchaseId:D}/payments/{firstLinkId:D}?fullWorthSpaceId={spaceId:D}",
            userId,
            new { amount = 5m, currency = "USD" }));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using (var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()))
        {
            Assert.Equal(5m, body.RootElement.GetProperty("amount").GetDecimal());
            Assert.Equal("EUR", body.RootElement.GetProperty("currency").GetString());
        }

        await factory.SeedAsync(async db =>
        {
            var link = await db.PurchasePaymentLinks.AsNoTracking().SingleAsync(x => x.Id == firstLinkId);
            Assert.Equal(5m, link.Amount);
            Assert.Equal("EUR", link.Currency);
        });
    }

    [Fact]
    public async Task Payment_candidates_are_empty_when_purchase_is_already_fully_paid()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var paidTransactionId = Guid.NewGuid();
        var otherTransactionId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();

        await SeedPaymentScenarioAsync(factory, userId, spaceId, accountId, paidTransactionId, "EUR", -10m, (purchaseId, 10m, "EUR"));
        await factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FinanceTransaction
            {
                Id = otherTransactionId,
                AccountId = accountId,
                ExternalKey = $"manual:{otherTransactionId:N}",
                Amount = -10m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, 8, 30),
                Counterparty = "Marketplace",
                RawJson = "{}"
            });
            db.PurchasePaymentLinks.Add(new PurchasePaymentLink
            {
                FullWorthSpaceId = spaceId,
                PurchaseId = purchaseId,
                TransactionId = paidTransactionId,
                Amount = 10m,
                Currency = "EUR",
                LinkSource = "manual",
                CreatedByUserId = userId
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/purchases/{purchaseId:D}/payment-candidates?fullWorthSpaceId={spaceId:D}",
            userId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0m, body.RootElement.GetProperty("remaining").GetDecimal());
        Assert.Empty(body.RootElement.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task Database_guard_rejects_direct_overallocation()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var firstPurchaseId = Guid.NewGuid();
        var secondPurchaseId = Guid.NewGuid();

        await SeedPaymentScenarioAsync(factory, userId, spaceId, accountId, transactionId, "EUR", -10m,
            (firstPurchaseId, 6m, "EUR"), (secondPurchaseId, 5m, "EUR"));

        await factory.SeedAsync(async db =>
        {
            db.PurchasePaymentLinks.Add(new PurchasePaymentLink
            {
                FullWorthSpaceId = spaceId,
                PurchaseId = firstPurchaseId,
                TransactionId = transactionId,
                Amount = 6m,
                Currency = "EUR",
                LinkSource = "manual",
                CreatedByUserId = userId
            });
            await db.SaveChangesAsync();

            db.PurchasePaymentLinks.Add(new PurchasePaymentLink
            {
                FullWorthSpaceId = spaceId,
                PurchaseId = secondPurchaseId,
                TransactionId = transactionId,
                Amount = 5m,
                Currency = "EUR",
                LinkSource = "manual",
                CreatedByUserId = userId
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Confirm_allows_two_purchases_when_allocations_exactly_match_shared_transaction()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var purchases = new[] { (Id: Guid.NewGuid(), Amount: 6m), (Id: Guid.NewGuid(), Amount: 4m) };

        await SeedPaymentScenarioAsync(factory, userId, spaceId, accountId, transactionId, "EUR", -10m,
            (purchases[0].Id, purchases[0].Amount, "EUR"), (purchases[1].Id, purchases[1].Amount, "EUR"));
        await factory.SeedAsync(async db =>
        {
            foreach (var row in purchases)
            {
                db.PurchaseItems.Add(new PurchaseItem { PurchaseId = row.Id, RawName = "Item", Name = "Item", Quantity = 1m, QuantityUnit = "piece", TotalPrice = row.Amount, Currency = "EUR" });
                db.PurchasePaymentLinks.Add(new PurchasePaymentLink { FullWorthSpaceId = spaceId, PurchaseId = row.Id, TransactionId = transactionId, Amount = row.Amount, Currency = "EUR", LinkSource = "manual", CreatedByUserId = userId });
            }
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        foreach (var row in purchases)
        {
            using var response = await client.SendAsync(UserRequest(
                HttpMethod.Post,
                $"/api/purchases/{row.Id:D}/confirm?fullWorthSpaceId={spaceId:D}",
                userId,
                new { createSafeAllocations = false, allowUnlinked = false }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task SeedPaymentScenarioAsync(
        BackendWebApplicationFactory factory,
        Guid userId,
        Guid spaceId,
        Guid accountId,
        Guid transactionId,
        string transactionCurrency,
        decimal transactionAmount,
        params (Guid PurchaseId, decimal Total, string Currency)[] purchases)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Payment allocation integrity",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Payment integrity", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = $"payment-integrity-{accountId:N}",
                ProviderAccountId = $"manual-{accountId:N}",
                InstitutionName = "Cash",
                DisplayName = "Wallet",
                Currency = transactionCurrency
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transactionId,
                AccountId = accountId,
                ExternalKey = $"manual:{transactionId:N}",
                Amount = transactionAmount,
                Currency = transactionCurrency,
                BookingDate = new DateOnly(2026, 8, 30),
                Counterparty = "Marketplace",
                RawJson = "{}"
            });
            foreach (var purchase in purchases)
            {
                db.Purchases.Add(new Purchase
                {
                    Id = purchase.PurchaseId,
                    FullWorthSpaceId = spaceId,
                    Merchant = "Marketplace",
                    Source = "manual",
                    PurchaseDate = new DateOnly(2026, 8, 30),
                    TotalAmount = purchase.Total,
                    Currency = purchase.Currency,
                    Status = "review",
                    ReviewState = "needs_review",
                    Visibility = "space",
                    CreatedByUserId = userId
                });
            }
            await db.SaveChangesAsync();

            // Purchase mutation routes require the purchases.manage capability; grant the acting member the
            // editor template (account ownership and the payment-allocation guard are enforced separately).
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
