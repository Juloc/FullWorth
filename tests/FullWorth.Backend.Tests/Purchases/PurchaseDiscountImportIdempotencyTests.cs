using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseDiscountImportIdempotencyTests
{
    [Fact]
    public async Task Identical_source_reimport_is_noop_but_real_change_resets_review_and_preserves_manual_rows()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var amazonDiscountId = Guid.NewGuid();
        var manualDiscountId = Guid.NewGuid();
        // Postgres timestamptz keeps microsecond precision while .NET DateTimeOffset carries 100ns ticks.
        // Truncate to whole microseconds so the seeded value round-trips exactly and the no-op reimport
        // assertion below compares like-for-like instead of tripping on sub-microsecond digits.
        var seedInstant = DateTimeOffset.UtcNow.AddMinutes(-10);
        var originalUpdatedAt = new DateTimeOffset(seedInstant.Ticks - (seedInstant.Ticks % 10), seedInstant.Offset);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Discount import user",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Discount import", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = spaceId,
                Source = "amazon",
                Merchant = "Amazon",
                PurchaseDate = new DateOnly(2026, 8, 31),
                TotalAmount = 17m,
                Currency = "EUR",
                DiscountAmount = 3m,
                Status = "confirmed",
                ReviewState = "confirmed",
                CreatedByUserId = userId,
                Visibility = "space",
                UpdatedAt = originalUpdatedAt
            });
            db.Set<PurchaseDiscount>().AddRange(
                new PurchaseDiscount
                {
                    Id = amazonDiscountId,
                    PurchaseId = purchaseId,
                    Type = "coupon",
                    Label = "Aktionsgutschein",
                    Amount = 2m,
                    RawText = "Aktionsgutschein -2,00 €",
                    Source = "amazon",
                    CreatedAt = originalUpdatedAt,
                    UpdatedAt = originalUpdatedAt
                },
                new PurchaseDiscount
                {
                    Id = manualDiscountId,
                    PurchaseId = purchaseId,
                    Type = "other",
                    Label = "Manuelle Korrektur",
                    Amount = 1m,
                    Source = "manual",
                    CreatedAt = originalUpdatedAt,
                    UpdatedAt = originalUpdatedAt
                });
            await db.SaveChangesAsync();
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseDiscountService>();
            await service.ReplaceSourceDiscountsAsync(spaceId, purchaseId, "amazon",
            [
                new PurchaseDiscountImport(
                    null, "coupon", "Aktionsgutschein", 2m, null, null,
                    "Aktionsgutschein -2,00 €", "amazon", null)
            ], CancellationToken.None);
        }

        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchaseId);
            var rows = await db.Set<PurchaseDiscount>().AsNoTracking().Where(x => x.PurchaseId == purchaseId).ToListAsync();
            Assert.Equal("confirmed", purchase.Status);
            Assert.Equal("confirmed", purchase.ReviewState);
            Assert.Equal(originalUpdatedAt, purchase.UpdatedAt);
            Assert.Contains(rows, x => x.Id == amazonDiscountId);
            Assert.Contains(rows, x => x.Id == manualDiscountId);
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PurchaseDiscountService>();
            await service.ReplaceSourceDiscountsAsync(spaceId, purchaseId, "amazon",
            [
                new PurchaseDiscountImport(
                    null, "coupon", "Aktionsgutschein", 3m, null, null,
                    "Aktionsgutschein -3,00 €", "amazon", null)
            ], CancellationToken.None);
        }

        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchaseId);
            var rows = await db.Set<PurchaseDiscount>().AsNoTracking().Where(x => x.PurchaseId == purchaseId).ToListAsync();
            Assert.Equal("review", purchase.Status);
            Assert.Equal("needs_review", purchase.ReviewState);
            Assert.Equal(4m, purchase.DiscountAmount);
            Assert.DoesNotContain(rows, x => x.Id == amazonDiscountId);
            Assert.Contains(rows, x => x.Id == manualDiscountId);
            Assert.Contains(rows, x => x.Source == "amazon" && x.Amount == 3m);
        });
    }
}