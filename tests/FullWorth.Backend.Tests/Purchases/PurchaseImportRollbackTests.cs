using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// Store/service-level coverage for the receipt-import rollback (#141): a dependent row blocks the
/// delete, an unencumbered purchase is removed together with its stored file, and a batch cannot be
/// rolled back twice. HTTP-level guard order (membership, capability, not-found) is covered separately
/// by ReceiptImportRollbackEndpointTests.
/// </summary>
public sealed class PurchaseImportRollbackTests
{
    [Fact]
    public async Task ADependentRowKeepsThePurchase()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, batchId, purchaseId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Seed(factory, userId, batchId, purchaseId);

        await factory.SeedAsync(async db =>
        {
            // PurchaseDifferenceAcceptances has no foreign key beyond the purchase itself, unlike
            // PurchasePaymentLinks (which requires a real Transaction row) - either represents genuine
            // user work the rollback must not silently discard.
            db.Set<PurchaseDifferenceAcceptance>().Add(new PurchaseDifferenceAcceptance
            {
                PurchaseId = purchaseId,
                Kind = "items",
                Amount = 1m,
                Reason = "other",
                AcceptedByUserId = userId
            });
            await db.SaveChangesAsync();
        });

        var (removed, filePaths) = await RollbackBatchAsync(factory, batchId);
        Assert.Equal(0, removed);
        Assert.Empty(filePaths);
        await factory.SeedAsync(async db => Assert.True(await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == purchaseId)));
    }

    [Fact]
    public async Task AnUnencumberedPurchaseIsRemovedWithItsStoredFile()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, batchId, purchaseId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Seed(factory, userId, batchId, purchaseId);

        const string relative = "rollback-test/receipt.jpg";
        var absolute = Path.Combine(factory.PurchaseStorageRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, [1, 2, 3]);
        await factory.SeedAsync(async db =>
        {
            db.PurchaseDocuments.Add(new PurchaseDocument
            {
                PurchaseId = purchaseId,
                DocumentType = "receipt",
                OriginalFileName = "receipt.jpg",
                MediaType = "image/jpeg",
                StoragePath = relative,
                Sha256 = new string('a', 64),
                SizeBytes = 3,
                Status = "uploaded"
            });
            await db.SaveChangesAsync();
        });

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ReceiptImportService>();
            var outcome = await service.RollbackBatchAsync(userId, FullWorthSpaceDefaults.LegacyId, batchId, CancellationToken.None);
            Assert.NotNull(outcome);
            Assert.Equal(1, outcome!.Removed);
            Assert.Equal(0, outcome.Kept);
        }

        Assert.False(File.Exists(absolute));
        await factory.SeedAsync(async db => Assert.False(await db.Purchases.AsNoTracking().AnyAsync(x => x.Id == purchaseId)));

        // Idempotent: a batch that has already been rolled back refuses a second attempt.
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ReceiptImportService>();
            await Assert.ThrowsAsync<ReceiptImportException>(() =>
                service.RollbackBatchAsync(userId, FullWorthSpaceDefaults.LegacyId, batchId, CancellationToken.None));
        }
    }

    private static async Task<(int Removed, IReadOnlyList<string> FilePaths)> RollbackBatchAsync(BackendWebApplicationFactory factory, Guid batchId)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ReceiptImportStore>();
        return await store.RollbackBatchAsync(batchId, CancellationToken.None);
    }

    private static async Task Seed(BackendWebApplicationFactory factory, Guid userId, Guid batchId, Guid purchaseId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Rollback owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Source = "receipt",
                Merchant = "Test Shop",
                TotalAmount = 12.34m,
                Currency = "EUR",
                Status = "review"
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ReceiptImportBatches"
                    ("Id", "FullWorthSpaceId", "UserId", "SourceType", "SourceName", "Currency", "Status", "AutoStart", "CreatedAt", "UpdatedAt")
                VALUES
                    ({batchId}, {FullWorthSpaceDefaults.LegacyId}, {userId}, {"upload"}, {"File upload"}, {"EUR"}, {ReceiptImportStatuses.Completed}, {false}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
                """);
            await PurchaseImportProvenance.LinkAsync(db, batchId, PurchaseImportSources.Receipt, [purchaseId], CancellationToken.None);
        });
    }
}
