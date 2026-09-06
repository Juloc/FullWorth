using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Purchases;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudProductPriceContributionServiceTests
{
    [Fact]
    public async Task Queues_one_minimized_price_observation_after_current_consent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var acceptedAt = await fixture.EnableCloudAsync();

        var purchase = fixture.AddPurchase(
            acceptedAt.AddMinutes(1),
            barcode: "4006381333931",
            unitPrice: 2.99m);
        await fixture.FinanceDb.SaveChangesAsync();

        var now = acceptedAt.AddMinutes(2);
        var queued = await fixture.Service.QueueCurrentAsync(now, CancellationToken.None);

        Assert.Equal(1, queued);
        var row = await fixture.IntelligenceDb.CloudSubmissionOutbox.SingleAsync();
        Assert.Equal("price_observation", row.EventType);
        Assert.Equal(CloudSubmissionStatuses.Queued, row.Status);

        using var doc = JsonDocument.Parse(row.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal("gtin:4006381333931", root.GetProperty("productKey").GetString());
        Assert.Equal(2.99m, root.GetProperty("unitPrice").GetDecimal());
        Assert.Equal("EUR", root.GetProperty("currency").GetString());
        Assert.Equal("DE", root.GetProperty("country").GetString());
        Assert.Equal("2026-09", root.GetProperty("observedMonth").GetString());
        Assert.Equal("purchase", root.GetProperty("source").GetString());

        var names = root.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "country", "currency", "observedMonth", "productKey", "source", "unitPrice" },
            names);

        var item = purchase.Items.Single();
        Assert.DoesNotContain(purchase.Id.ToString(), row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(item.Id.ToString(), row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Space.Id.ToString(), row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REWE", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(purchase.Id.ToString(), row.IdempotencyKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(item.Id.ToString(), row.IdempotencyKey, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, await fixture.Service.QueueCurrentAsync(now, CancellationToken.None));
        Assert.Equal(1, await fixture.IntelligenceDb.CloudSubmissionOutbox.CountAsync());
    }

    [Fact]
    public async Task Purchase_existing_before_current_consent_is_not_backfilled()
    {
        await using var fixture = await Fixture.CreateAsync();

        var historicalTime = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        fixture.AddPurchase(
            historicalTime,
            barcode: "4006381333931",
            unitPrice: 2.99m);
        await fixture.FinanceDb.SaveChangesAsync();

        var acceptedAt = await fixture.EnableCloudAsync();

        var queued = await fixture.Service.QueueCurrentAsync(
            acceptedAt.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(0, queued);
        Assert.Empty(await fixture.IntelligenceDb.CloudSubmissionOutbox.ToListAsync());
    }

    [Fact]
    public async Task Invalid_gtin_never_becomes_cloud_price_observation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var acceptedAt = await fixture.EnableCloudAsync();

        fixture.AddPurchase(
            acceptedAt.AddMinutes(1),
            barcode: "4006381333930",
            unitPrice: 2.99m);
        await fixture.FinanceDb.SaveChangesAsync();

        Assert.Equal(
            0,
            await fixture.Service.QueueCurrentAsync(
                acceptedAt.AddMinutes(2),
                CancellationToken.None));
        Assert.Empty(await fixture.IntelligenceDb.CloudSubmissionOutbox.ToListAsync());
    }

    [Fact]
    public async Task Sent_purchase_item_is_not_uploaded_again_after_local_price_correction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var acceptedAt = await fixture.EnableCloudAsync();

        var purchase = fixture.AddPurchase(
            acceptedAt.AddMinutes(1),
            barcode: "4006381333931",
            unitPrice: 2.99m);
        await fixture.FinanceDb.SaveChangesAsync();

        Assert.Equal(
            1,
            await fixture.Service.QueueCurrentAsync(
                acceptedAt.AddMinutes(2),
                CancellationToken.None));

        var outbox = await fixture.IntelligenceDb.CloudSubmissionOutbox.SingleAsync();
        var firstPayload = outbox.PayloadJson;
        outbox.Status = CloudSubmissionStatuses.Sent;
        outbox.SentAt = acceptedAt.AddMinutes(3);
        await fixture.IntelligenceDb.SaveChangesAsync();

        var item = await fixture.FinanceDb.PurchaseItems.SingleAsync(x => x.Id == purchase.Items.Single().Id);
        item.UnitPrice = 3.49m;
        item.UpdatedAt = acceptedAt.AddMinutes(4);
        await fixture.FinanceDb.SaveChangesAsync();

        Assert.Equal(
            0,
            await fixture.Service.QueueCurrentAsync(
                acceptedAt.AddMinutes(5),
                CancellationToken.None));

        var rows = await fixture.IntelligenceDb.CloudSubmissionOutbox.ToListAsync();
        Assert.Single(rows);
        Assert.Equal(firstPayload, rows[0].PayloadJson);
        Assert.DoesNotContain("3.49", rows[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_base_or_quantity_unit_price_when_explicit_unit_price_missing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var acceptedAt = await fixture.EnableCloudAsync();

        fixture.AddPurchase(
            acceptedAt.AddMinutes(1),
            barcode: "4006381333931",
            unitPrice: null,
            baseUnitPrice: 1.25m,
            quantity: 2m,
            totalPrice: 5m);
        await fixture.FinanceDb.SaveChangesAsync();

        Assert.Equal(
            1,
            await fixture.Service.QueueCurrentAsync(
                acceptedAt.AddMinutes(2),
                CancellationToken.None));

        using var doc = JsonDocument.Parse(
            (await fixture.IntelligenceDb.CloudSubmissionOutbox.SingleAsync()).PayloadJson);
        Assert.Equal(1.25m, doc.RootElement.GetProperty("unitPrice").GetDecimal());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection financeConnection;
        private readonly SqliteConnection intelligenceConnection;
        private readonly CloudIntelligenceStateService cloudState;

        public FullWorthDbContext FinanceDb { get; }
        public IntelligenceDbContext IntelligenceDb { get; }
        public FullWorthSpace Space { get; }
        public CloudProductPriceContributionService Service { get; }

        private Fixture(
            SqliteConnection financeConnection,
            SqliteConnection intelligenceConnection,
            FullWorthDbContext financeDb,
            IntelligenceDbContext intelligenceDb,
            FullWorthSpace space,
            CloudIntelligenceStateService cloudState)
        {
            this.financeConnection = financeConnection;
            this.intelligenceConnection = intelligenceConnection;
            FinanceDb = financeDb;
            IntelligenceDb = intelligenceDb;
            Space = space;
            this.cloudState = cloudState;
            Service = new CloudProductPriceContributionService(
                financeDb,
                intelligenceDb,
                cloudState);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var financeConnection = new SqliteConnection("Data Source=:memory:");
            await financeConnection.OpenAsync();
            var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
            await intelligenceConnection.OpenAsync();

            var financeDb = new FullWorthDbContext(
                new DbContextOptionsBuilder<FullWorthDbContext>()
                    .UseSqlite(financeConnection)
                    .Options);
            var intelligenceDb = new IntelligenceDbContext(
                new DbContextOptionsBuilder<IntelligenceDbContext>()
                    .UseSqlite(intelligenceConnection)
                    .Options);
            await financeDb.Database.EnsureCreatedAsync();
            await intelligenceDb.Database.EnsureCreatedAsync();

            var space = new FullWorthSpace
            {
                Name = "Household",
                BaseCurrency = "EUR"
            };
            financeDb.FullWorthSpaces.Add(space);
            financeDb.BankConnections.Add(new BankConnection
            {
                FullWorthSpaceId = space.Id,
                InstitutionName = "Test Bank",
                Country = "DE"
            });
            await financeDb.SaveChangesAsync();

            var cloudState = new CloudIntelligenceStateService(intelligenceDb);
            return new Fixture(
                financeConnection,
                intelligenceConnection,
                financeDb,
                intelligenceDb,
                space,
                cloudState);
        }

        public async Task<DateTimeOffset> EnableCloudAsync()
        {
            await cloudState.EnableAsync(
                Guid.NewGuid(),
                new EnableCloudIntelligenceRequest(
                    CloudIntelligencePolicy.CurrentVersion,
                    "de",
                    "test"),
                CancellationToken.None);

            var state = await cloudState.GetAsync(CancellationToken.None);
            return state.AcceptedAt!.Value;
        }

        public Purchase AddPurchase(
            DateTimeOffset updatedAt,
            string barcode,
            decimal? unitPrice,
            decimal? baseUnitPrice = null,
            decimal quantity = 1m,
            decimal? totalPrice = null)
        {
            var purchase = new Purchase
            {
                FullWorthSpaceId = Space.Id,
                Source = "receipt",
                Merchant = "REWE",
                PurchaseDate = new DateOnly(2026, 9, 6),
                TotalAmount = totalPrice ?? unitPrice ?? baseUnitPrice ?? 0m,
                Currency = "EUR",
                Status = "confirmed",
                ReviewState = "confirmed",
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            };
            purchase.Items.Add(new PurchaseItem
            {
                Barcode = barcode,
                RawName = "Test Product",
                Name = "Test Product",
                Quantity = quantity,
                UnitPrice = unitPrice,
                BaseUnitPrice = baseUnitPrice,
                TotalPrice = totalPrice ?? unitPrice ?? baseUnitPrice ?? 0m,
                Currency = "EUR",
                LineType = "product",
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            });
            FinanceDb.Purchases.Add(purchase);
            return purchase;
        }

        public async ValueTask DisposeAsync()
        {
            await FinanceDb.DisposeAsync();
            await IntelligenceDb.DisposeAsync();
            await financeConnection.DisposeAsync();
            await intelligenceConnection.DisposeAsync();
        }
    }
}
