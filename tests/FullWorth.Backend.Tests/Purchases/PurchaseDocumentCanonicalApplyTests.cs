using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Purchases.Extraction;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseDocumentCanonicalApplyTests
{
    [Fact]
    public async Task CompletedDocumentRunAppliesOriginalPriceDiscountDepositShippingAndRoundingCanonically()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var extracted = new ReceiptExtractionResult(
            Provider: "fake",
            Merchant: "Test Markt",
            PurchaseDate: new DateOnly(2026, 8, 31),
            Currency: "EUR",
            Total: 10.24m,
            Discounts: 3m,
            Deposits: .25m,
            Taxes: 1m,
            Items:
            [
                new ReceiptLineItem(
                    Name: "Ware",
                    Quantity: 1m,
                    UnitPrice: 10m,
                    TotalPrice: 10m,
                    CategoryHint: null,
                    Confidence: .95m,
                    QuantityUnit: "piece",
                    OriginalUnitPrice: 12m,
                    DiscountAmount: 2m,
                    DiscountLabel: "Aktionspreis",
                    DepositAmount: .25m,
                    LineType: "product")
            ],
            Confidence: .9m,
            Subtotal: 12m,
            Rounding: -.01m,
            Shipping: 1m,
            StructuredDiscounts:
            [
                new ReceiptDiscount("price_reduction", "Aktionspreis", 2m, Confidence: .95m, ItemIndex: 0),
                new ReceiptDiscount("coupon", "App Coupon", 1m, CouponCode: "APP1", Confidence: .9m)
            ]);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Document user", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Document Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            var purchase = new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = spaceId,
                Source = "receipt",
                Merchant = "Old Merchant",
                TotalAmount = 1m,
                Currency = "EUR",
                Status = "review",
                ReviewState = "needs_review",
                CreatedByUserId = userId,
                Visibility = "space"
            };
            purchase.Documents.Add(new PurchaseDocument
            {
                Id = documentId,
                PurchaseId = purchaseId,
                DocumentType = "receipt",
                OriginalFileName = "receipt.png",
                MediaType = "image/png",
                StoragePath = "fixture/receipt.png",
                Sha256 = new string('a', 64),
                SizeBytes = 12,
                Status = "review"
            });
            db.Purchases.Add(purchase);
            db.PurchaseExtractionRuns.Add(new PurchaseExtractionRun
            {
                Id = runId,
                PurchaseDocumentId = documentId,
                Provider = "fake",
                Status = "completed",
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                NormalizedResultJson = JsonSerializer.Serialize(extracted),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseDocumentService>();
            var result = await service.ApplyRunAsync(
                userId,
                spaceId,
                purchaseId,
                runId,
                new ApplyExtractionRunRequest(ApplyMerchant: true, ApplyDate: true, ApplyTotal: true, ApplyCurrency: true, ReplaceItems: true),
                CancellationToken.None);
            Assert.Equal(PurchaseMutationResult.Success, result.Result);
        }

        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases
                .Include(x => x.Items)
                .Include(x => x.Discounts)
                .SingleAsync(x => x.Id == purchaseId);
            Assert.Equal("Test Markt", purchase.Merchant);
            Assert.Equal(new DateOnly(2026, 8, 31), purchase.PurchaseDate);
            Assert.Equal(12m, purchase.SubtotalAmount);
            Assert.Equal(3m, purchase.DiscountAmount);
            Assert.Equal(.25m, purchase.DepositAmount);
            Assert.Equal(1m, purchase.ShippingAmount);
            Assert.Equal(-.01m, purchase.RoundingAmount);
            Assert.Equal(10.24m, purchase.TotalAmount);
            Assert.Equal("needs_review", purchase.ReviewState);

            var item = Assert.Single(purchase.Items);
            Assert.Equal(10m, item.UnitPrice);
            Assert.Equal(12m, item.OriginalUnitPrice);
            Assert.Equal(10m, item.TotalPrice);
            Assert.Equal(2m, item.DiscountAmount);
            Assert.Equal("Aktionspreis", item.DiscountLabel);
            Assert.Equal(.25m, item.DepositAmount);

            Assert.Equal(2, purchase.Discounts.Count);
            var itemDiscount = purchase.Discounts.Single(x => x.Type == "price_reduction");
            Assert.Equal(item.Id, itemDiscount.PurchaseItemId);
            Assert.Equal(2m, itemDiscount.Amount);
            var basket = purchase.Discounts.Single(x => x.Type == "coupon");
            Assert.Null(basket.PurchaseItemId);
            Assert.Equal(1m, basket.Amount);
        });
    }
}
