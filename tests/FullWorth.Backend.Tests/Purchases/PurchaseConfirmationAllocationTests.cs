using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseConfirmationAllocationTests
{
    [Fact]
    public async Task Confirm_creates_product_and_coupon_allocations_with_opposite_ledger_signs()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var productItemId = Guid.NewGuid();
        var couponItemId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Signed confirm",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Signed confirm", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = $"signed-confirm-{accountId:N}",
                ProviderAccountId = $"manual-{accountId:N}",
                InstitutionName = "Cash",
                DisplayName = "Wallet",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transactionId,
                AccountId = accountId,
                ExternalKey = $"manual:{transactionId:N}",
                Amount = -13m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, 8, 30),
                Counterparty = "Shop",
                RawJson = "{}"
            });
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = spaceId,
                Merchant = "Shop",
                Source = "receipt",
                PurchaseDate = new DateOnly(2026, 8, 30),
                TotalAmount = 13m,
                Currency = "EUR",
                Status = "review",
                ReviewState = "needs_review",
                CreatedByUserId = userId,
                Visibility = "space"
            });
            db.PurchaseItems.AddRange(
                new PurchaseItem
                {
                    Id = productItemId,
                    PurchaseId = purchaseId,
                    RawName = "Product",
                    Name = "Product",
                    Quantity = 1m,
                    QuantityUnit = "piece",
                    TotalPrice = 15m,
                    Currency = "EUR",
                    LineType = "product",
                    SortOrder = 0
                },
                new PurchaseItem
                {
                    Id = couponItemId,
                    PurchaseId = purchaseId,
                    RawName = "Coupon",
                    Name = "Coupon",
                    Quantity = 1m,
                    QuantityUnit = "piece",
                    TotalPrice = -2m,
                    Currency = "EUR",
                    LineType = "coupon",
                    SortOrder = 1
                });
            db.PurchasePaymentLinks.Add(new PurchasePaymentLink
            {
                FullWorthSpaceId = spaceId,
                PurchaseId = purchaseId,
                TransactionId = transactionId,
                Amount = 13m,
                Currency = "EUR",
                LinkSource = "manual",
                CreatedByUserId = userId
            });
            await db.SaveChangesAsync();

            // Purchase mutation routes require the purchases.manage capability; grant the acting member the
            // editor template (account ownership is still enforced separately).
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/purchases/{purchaseId:D}/confirm?fullWorthSpaceId={spaceId:D}",
            userId,
            new { createSafeAllocations = true, allowUnlinked = false }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchaseId);
            var allocations = await db.TransactionAllocations.AsNoTracking()
                .Where(x => x.TransactionId == transactionId)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync();

            Assert.Equal("confirmed", purchase.Status);
            Assert.Equal("confirmed", purchase.ReviewState);
            Assert.Equal(2, allocations.Count);
            Assert.Equal(-15m, allocations.Single(x => x.PurchaseItemId == productItemId).Amount);
            Assert.Equal(2m, allocations.Single(x => x.PurchaseItemId == couponItemId).Amount);
            Assert.Equal(-13m, allocations.Sum(x => x.Amount));
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        request.Content = JsonContent.Create(body);
        return request;
    }
}
