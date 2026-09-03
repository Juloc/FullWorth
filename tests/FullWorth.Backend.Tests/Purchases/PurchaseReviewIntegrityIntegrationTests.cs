using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseReviewIntegrityIntegrationTests
{
    [Fact]
    public async Task ConfirmedPurchase_ItemMutation_ReopensReviewAndClearsAcceptedDifferences()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, user, _, _, _, purchase, item) = await SeedPurchaseAsync(factory, "EUR", "EUR", 10m);
        await factory.SeedAsync(async db =>
        {
            var p = await db.Purchases.SingleAsync(x => x.Id == purchase);
            p.Status = "confirmed";
            p.ReviewState = "confirmed";
            db.PurchaseDifferenceAcceptances.Add(new PurchaseDifferenceAcceptance
            {
                PurchaseId = purchase,
                Kind = "items",
                Amount = 0m,
                Reason = "rounding",
                AcceptedByUserId = user
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(HttpMethod.Patch,
            $"/api/purchases/{purchase:D}/items/{item:D}?fullWorthSpaceId={space:D}", user,
            ItemPatch(10m, "Updated item")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var p = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchase);
            Assert.Equal("review", p.Status);
            Assert.Equal("needs_review", p.ReviewState);
            Assert.False(await db.PurchaseDifferenceAcceptances.AnyAsync(x => x.PurchaseId == purchase));
        });
    }

    [Fact]
    public async Task PaymentLink_UsesTransactionCurrency_AndForeignCurrencyBlocksConfirmation()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, user, _, _, transaction, purchase, _) = await SeedPurchaseAsync(factory, "EUR", "USD", 10m);
        using var client = factory.CreateClient();

        using var linkResponse = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{purchase:D}/payments?fullWorthSpaceId={space:D}", user,
            new { transactionId = transaction, amount = 10m, currency = "EUR", linkSource = "manual", confidence = .99m }));
        Assert.Equal(HttpStatusCode.Created, linkResponse.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var link = await db.PurchasePaymentLinks.AsNoTracking().SingleAsync(x => x.PurchaseId == purchase);
            Assert.Equal("USD", link.Currency);
        });

        using var confirmResponse = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{purchase:D}/confirm?fullWorthSpaceId={space:D}", user,
            new { createSafeAllocations = true, allowUnlinked = true }));
        Assert.Equal(HttpStatusCode.Conflict, confirmResponse.StatusCode);
        Assert.Contains("foreign_currency_payment", await confirmResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconfirm_ReplacesOnlyAllocationsOwnedByCurrentPurchaseItems()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, user, _, _, transaction, purchase, firstItem) = await SeedPurchaseAsync(factory, "EUR", "EUR", 10m, firstItemTotal: 6m);
        var secondItem = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.PurchaseItems.Add(new PurchaseItem
            {
                Id = secondItem,
                PurchaseId = purchase,
                RawName = "Second",
                Name = "Second",
                Quantity = 1m,
                QuantityUnit = "piece",
                TotalPrice = 4m,
                Currency = "EUR",
                SortOrder = 1
            });
            db.PurchasePaymentLinks.Add(new PurchasePaymentLink
            {
                FullWorthSpaceId = space,
                PurchaseId = purchase,
                TransactionId = transaction,
                Amount = 10m,
                Currency = "EUR",
                LinkSource = "manual",
                CreatedByUserId = user
            });
            db.TransactionAllocations.AddRange(
                new TransactionAllocation { TransactionId = transaction, Amount = -7m, Note = "Old first", PurchaseItemId = firstItem },
                new TransactionAllocation { TransactionId = transaction, Amount = -3m, Note = "Old second", PurchaseItemId = secondItem });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{purchase:D}/confirm?fullWorthSpaceId={space:D}", user,
            new { createSafeAllocations = true, allowUnlinked = true }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var allocations = await db.TransactionAllocations.AsNoTracking().Where(x => x.TransactionId == transaction).OrderBy(x => x.Amount).ToListAsync();
            Assert.Equal(2, allocations.Count);
            Assert.Contains(allocations, x => x.PurchaseItemId == firstItem && x.Amount == -6m);
            Assert.Contains(allocations, x => x.PurchaseItemId == secondItem && x.Amount == -4m);
            var p = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchase);
            Assert.Equal("confirmed", p.Status);
            Assert.Equal("confirmed", p.ReviewState);
        });
    }

    [Fact]
    public async Task Reconfirm_DoesNotOverwriteManualAllocation()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, user, _, _, transaction, purchase, _) = await SeedPurchaseAsync(factory, "EUR", "EUR", 10m);
        await factory.SeedAsync(async db =>
        {
            db.PurchasePaymentLinks.Add(new PurchasePaymentLink
            {
                FullWorthSpaceId = space,
                PurchaseId = purchase,
                TransactionId = transaction,
                Amount = 10m,
                Currency = "EUR",
                LinkSource = "manual",
                CreatedByUserId = user
            });
            db.TransactionAllocations.Add(new TransactionAllocation
            {
                TransactionId = transaction,
                Amount = -10m,
                Note = "Manual split",
                PurchaseItemId = null
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{purchase:D}/confirm?fullWorthSpaceId={space:D}", user,
            new { createSafeAllocations = true, allowUnlinked = true }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("existing_allocations", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await factory.SeedAsync(async db =>
        {
            var allocation = await db.TransactionAllocations.AsNoTracking().SingleAsync(x => x.TransactionId == transaction);
            Assert.Null(allocation.PurchaseItemId);
            Assert.Equal(-10m, allocation.Amount);
        });
    }

    private static async Task<(Guid Space, Guid User, Guid Connection, Guid Account, Guid Transaction, Guid Purchase, Guid Item)> SeedPurchaseAsync(
        BackendWebApplicationFactory factory,
        string purchaseCurrency,
        string transactionCurrency,
        decimal total,
        decimal? firstItemTotal = null)
    {
        var user = Guid.NewGuid();
        var space = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();
        var transaction = Guid.NewGuid();
        var purchase = Guid.NewGuid();
        var item = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Purchase integrity",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Integrity", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = user, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection
            {
                Id = connection,
                FullWorthSpaceId = space,
                Provider = "test",
                InstitutionName = "Test Bank",
                Country = "DE",
                ProviderSessionId = $"integrity-{connection:N}",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = space,
                BankConnectionId = connection,
                Provider = "test",
                IdentificationHash = $"integrity-{account:N}",
                ProviderAccountId = $"provider-{account:N}",
                InstitutionName = "Test Bank",
                DisplayName = "Card",
                Currency = transactionCurrency
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = user, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transaction,
                AccountId = account,
                ExternalKey = $"integrity-{transaction:N}",
                Amount = -total,
                Currency = transactionCurrency,
                BookingDate = new DateOnly(2026, 8, 30),
                Counterparty = "Shop",
                RawJson = "{}"
            });
            db.Purchases.Add(new Purchase
            {
                Id = purchase,
                FullWorthSpaceId = space,
                Merchant = "Shop",
                Source = "receipt",
                PurchaseDate = new DateOnly(2026, 8, 30),
                TotalAmount = total,
                Currency = purchaseCurrency,
                Status = "review",
                ReviewState = "needs_review",
                CreatedByUserId = user,
                Visibility = "space"
            });
            db.PurchaseItems.Add(new PurchaseItem
            {
                Id = item,
                PurchaseId = purchase,
                RawName = "First",
                Name = "First",
                Quantity = 1m,
                QuantityUnit = "piece",
                TotalPrice = firstItemTotal ?? total,
                Currency = purchaseCurrency,
                SortOrder = 0
            });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member owns the account/purchase but resolves to
            // the read-only viewer template, so grant editor to reach the /api/purchases write handlers.
            await CapabilityTestSeeding.GrantEditorAsync(db, space, user);
        });
        return (space, user, connection, account, transaction, purchase, item);
    }

    private static object ItemPatch(decimal total, string name) => new
    {
        productId = (Guid?)null,
        categoryId = (Guid?)null,
        name,
        rawName = name,
        brand = (string?)null,
        sku = (string?)null,
        barcode = (string?)null,
        asin = (string?)null,
        quantity = 1m,
        quantityUnit = "piece",
        packageQuantity = (decimal?)null,
        packageUnit = (string?)null,
        packageCount = (decimal?)null,
        unitPrice = total,
        totalPrice = total,
        baseUnitPrice = (decimal?)null,
        discountAmount = (decimal?)null,
        depositAmount = (decimal?)null,
        taxRate = (decimal?)null,
        taxAmount = (decimal?)null,
        currency = "EUR",
        lineType = "product",
        notes = (string?)null,
        sortOrder = 0,
        returnDeadline = (DateOnly?)null,
        warrantyEnd = (DateOnly?)null,
        serialNumber = (string?)null,
        totalPriceOverridden = true
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
