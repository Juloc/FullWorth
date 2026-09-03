using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseSemanticDuplicateDetectorTests
{
    [Fact]
    public async Task SameMerchantDateAmountAndReceiptNumberProducesWarningWithoutMutatingPurchases()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        await SeedMemberAndExistingAsync(factory, userId, spaceId, existingId, visibility: "space", createdBy: userId);

        using var scope = factory.Services.CreateScope();
        var detector = scope.ServiceProvider.GetRequiredService<PurchaseSemanticDuplicateDetector>();
        var warnings = await detector.DetectWarningsAsync(userId, spaceId, currentId, Request("4711"), CancellationToken.None);

        var warning = Assert.Single(warnings);
        Assert.Contains("doppelter Beleg", warning, StringComparison.OrdinalIgnoreCase);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, db.Purchases.Count(x => x.FullWorthSpaceId == spaceId));
            Assert.Equal(2, db.PurchaseItems.Count(x => x.PurchaseId == existingId));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StrongItemOverlapWarnsWhenReceiptNumberIsUnavailable()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await SeedMemberAndExistingAsync(factory, userId, spaceId, Guid.NewGuid(), visibility: "space", createdBy: userId, receiptNumber: null);

        using var scope = factory.Services.CreateScope();
        var detector = scope.ServiceProvider.GetRequiredService<PurchaseSemanticDuplicateDetector>();
        var warnings = await detector.DetectWarningsAsync(userId, spaceId, Guid.NewGuid(), Request(null), CancellationToken.None);

        Assert.Single(warnings);
        Assert.Contains("mehrere Artikel", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnotherUsersPrivatePurchaseDoesNotLeakThroughDuplicateWarning()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                User(userId, "member"),
                User(otherUserId, "other"));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Dup Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = otherUserId, Role = FullWorthSpaceRoles.Member });
            var hidden = ExistingPurchase(spaceId, otherUserId, "private", "4711");
            db.Purchases.Add(hidden);
            await db.SaveChangesAsync();
        });

        using var scope = factory.Services.CreateScope();
        var detector = scope.ServiceProvider.GetRequiredService<PurchaseSemanticDuplicateDetector>();
        var warnings = await detector.DetectWarningsAsync(userId, spaceId, Guid.NewGuid(), Request("4711"), CancellationToken.None);

        Assert.Empty(warnings);
    }

    private static async Task SeedMemberAndExistingAsync(
        BackendWebApplicationFactory factory,
        Guid userId,
        Guid spaceId,
        Guid purchaseId,
        string visibility,
        Guid createdBy,
        string? receiptNumber = "4711")
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(User(userId, "member"));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Dup Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            var purchase = ExistingPurchase(spaceId, createdBy, visibility, receiptNumber);
            purchase.Id = purchaseId;
            db.Purchases.Add(purchase);
            await db.SaveChangesAsync();
        });
    }

    private static Purchase ExistingPurchase(Guid spaceId, Guid createdBy, string visibility, string? receiptNumber) => new()
    {
        FullWorthSpaceId = spaceId,
        Source = "receipt",
        Merchant = "REWE Markt GmbH",
        PurchaseDate = new DateOnly(2026, 8, 31),
        TotalAmount = 12.34m,
        Currency = "EUR",
        ReceiptNumber = receiptNumber,
        Visibility = visibility,
        CreatedByUserId = createdBy,
        Status = "review",
        ReviewState = "needs_review",
        Items =
        [
            Item("Bio Milch", 4.35m),
            Item("Barilla Spaghetti", 7.99m)
        ]
    };

    private static PurchaseItem Item(string name, decimal total) => new()
    {
        RawName = name,
        Name = name,
        Quantity = 1m,
        QuantityUnit = "piece",
        TotalPrice = total,
        Currency = "EUR",
        LineType = "product",
        CategorizationSource = "none"
    };

    private static PurchaseExtractionRequest Request(string? receiptNumber) => new(
        Merchant: "REWE Markt GmbH",
        PurchaseDate: new DateOnly(2026, 8, 31),
        TotalAmount: 12.34m,
        Currency: "EUR",
        Items:
        [
            new PurchaseItemWrite(null, "Bio Milch", null, null, null, 1m, 4.35m, 4.35m, "EUR", null),
            new PurchaseItemWrite(null, "Barilla Spaghetti", null, null, null, 1m, 7.99m, 7.99m, "EUR", null)
        ],
        SourceReference: "test",
        Notes: null,
        ReceiptNumber: receiptNumber,
        AmountsAreCanonical: true);

    private static FullWorthUser User(Guid id, string name) => new()
    {
        Id = id,
        EmailNormalized = $"{id:N}@EXAMPLE.COM",
        DisplayName = name,
        IsActive = true
    };
}
