using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Notifications;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Push;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Notifications;

public sealed class PurchaseNotificationServiceTests
{
    private sealed class FakePushSender : IPushSender
    {
        public List<(Guid UserId, PushMessage Message)> Sent { get; } = [];
        public Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct)
        {
            Sent.Add((userId, message));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PurchaseAlertsUseRealErrorStatusDedupAndNeverBroadcastLegacyRows()
    {
        var now = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var legacyPurchaseId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = userId, EmailNormalized = "PURCHASE@EXAMPLE.COM", DisplayName = "Purchase owner", IsActive = true },
                new FullWorthUser { Id = otherUserId, EmailNormalized = "OTHER@EXAMPLE.COM", DisplayName = "Other member", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Purchase notification space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = otherUserId, Role = FullWorthSpaceRoles.Member });

            var purchase = new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = spaceId,
                Source = "receipt",
                Merchant = "Testmarkt",
                Currency = "EUR",
                TotalAmount = 12.34m,
                Status = "review",
                ReviewState = "needs_review",
                Visibility = "space",
                CreatedByUserId = userId,
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2)
            };
            purchase.Items.Add(new PurchaseItem
            {
                Name = "Kopfhörer",
                RawName = "Kopfhörer",
                Quantity = 1,
                QuantityUnit = "piece",
                TotalPrice = 12.34m,
                Currency = "EUR",
                LineType = "product",
                ReturnDeadline = DateOnly.FromDateTime(now.UtcDateTime).AddDays(2),
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2)
            });
            db.Purchases.Add(purchase);
            db.Purchases.Add(new Purchase
            {
                Id = legacyPurchaseId,
                FullWorthSpaceId = spaceId,
                Source = "receipt",
                Merchant = "Legacy Secret",
                Currency = "EUR",
                TotalAmount = 99m,
                Status = "review",
                ReviewState = "needs_review",
                Visibility = "space",
                CreatedByUserId = null,
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2)
            });
            await db.SaveChangesAsync();

            var jobs = new ReceiptScanJobStore(db);
            await jobs.CreateAsync(new ReceiptScanJobRow
            {
                Id = jobId,
                FullWorthSpaceId = spaceId,
                UserId = userId,
                PurchaseId = purchaseId,
                FileName = "receipt.png",
                ContentType = "image/png",
                Status = ReceiptScanJobStatuses.Error,
                Stage = "error",
                Attempts = 2,
                CreatedAt = now.AddHours(-1),
                UpdatedAt = now.AddHours(-1)
            }, CancellationToken.None);
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var fake = new FakePushSender();
        var dispatcher = new NotificationDispatcher(db, new PreferenceStore(db), fake, NullLogger<NotificationDispatcher>.Instance);
        var service = new PurchaseNotificationService(db, dispatcher);

        await service.ScanAndDispatchAsync(now, CancellationToken.None);

        Assert.Contains(fake.Sent, row => row.UserId == userId && row.Message.Title.Contains("Beleg", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fake.Sent, row => row.UserId == userId && row.Message.Title.Contains("prüfen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fake.Sent, row => row.UserId == userId && row.Message.Title.Contains("verknüpft", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fake.Sent, row => row.UserId == userId && row.Message.Title.Contains("Rückgabefrist", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fake.Sent, row => row.UserId == otherUserId);
        Assert.DoesNotContain(fake.Sent, row => row.Message.Body.Contains("Legacy Secret", StringComparison.OrdinalIgnoreCase));

        var firstCount = fake.Sent.Count;
        await service.ScanAndDispatchAsync(now, CancellationToken.None);
        Assert.Equal(firstCount, fake.Sent.Count);
    }
}
