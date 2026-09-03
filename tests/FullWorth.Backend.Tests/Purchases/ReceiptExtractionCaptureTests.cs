using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Purchases.Extraction;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// Wave: receipt-scan now runs the configured extractor and pre-fills the purchase. These cover the
/// merge precedence and both direct-capture outcomes. The configured-provider tests also assert that
/// local OCR uses the same canonical discount/deposit/rounding model as the durable queue.
/// </summary>
public sealed class ReceiptExtractionCaptureTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Fact]
    public void Merge_PrefersFormValuesOverOcr()
    {
        var ocr = new ReceiptExtractionResult("fake", "Rewe", new DateOnly(2026, 1, 1), "EUR", 5m, null, null, null, [], 0.5m);
        var merged = PurchaseCaptureService.MergeCaptured("Aldi", new DateOnly(2026, 8, 15), 9.99m, "EUR", ocr);
        Assert.Equal("Aldi", merged.Merchant);
        Assert.Equal(new DateOnly(2026, 8, 15), merged.Date);
        Assert.Equal(9.99m, merged.Total);
    }

    [Fact]
    public void Merge_FillsGapsFromOcr()
    {
        var ocr = new ReceiptExtractionResult("fake", "Rewe", new DateOnly(2026, 8, 12), "EUR", 12.50m, null, null, null, [], 0.5m);
        var merged = PurchaseCaptureService.MergeCaptured("", null, 0m, "EUR", ocr);
        Assert.Equal("Rewe", merged.Merchant);
        Assert.Equal(new DateOnly(2026, 8, 12), merged.Date);
        Assert.Equal(12.50m, merged.Total);
    }

    [Fact]
    public void Merge_KeepsFormCurrency()
    {
        var ocr = new ReceiptExtractionResult("fake", null, null, "EUR", null, null, null, null, [], 0.5m);
        var merged = PurchaseCaptureService.MergeCaptured("Shop", null, 1m, "USD", ocr);
        Assert.Equal("USD", merged.Currency);
    }

    [Fact]
    public async Task ReceiptScan_WithConfiguredProvider_PopulatesPurchaseAndItems()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, member) = await SeedMemberAsync(factory);

        var result = new ReceiptExtractionResult("fake", "Rewe", new DateOnly(2026, 8, 15), null, 12.50m, null, null, null,
            [new ReceiptLineItem("Milk", 1m, 3.00m, 3.00m, null, 0.9m)], 0.8m);
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ReceiptExtraction:Provider"] = "fake" }));
            b.ConfigureTestServices(s => s.AddSingleton<IReceiptExtractor>(new FakeExtractor("fake", result)));
        });
        using var client = configured.CreateClient();

        using var response = await client.SendAsync(ReceiptRequest(space, member, includeManualFields: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<PurchaseViewProbe>();
        Assert.Equal("Rewe", view!.Merchant);
        Assert.Equal(12.50m, view.TotalAmount);
        Assert.Equal("review", view.Status);
        Assert.Single(view.Items);
        Assert.Equal("Milk", view.Items[0].Name);
    }

    [Fact]
    public async Task ReceiptScan_WithCanonicalLocalAdjustments_DoesNotCreateFakeItems()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, member) = await SeedMemberAsync(factory);
        var result = new ReceiptExtractionResult(
            "fake", "Markt", new DateOnly(2026, 8, 31), "EUR", 9.24m, 2m, .25m, null,
            [new ReceiptLineItem("Ware", 1m, 10m, 10m, null, .95m, "piece")], .95m,
            Subtotal: 10m,
            Rounding: -.01m,
            Shipping: 1m,
            StructuredDiscounts: [new ReceiptDiscount("coupon", "Aktionsgutschein", 2m, Confidence: .9m)]);
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ReceiptExtraction:Provider"] = "fake" }));
            b.ConfigureTestServices(s => s.AddSingleton<IReceiptExtractor>(new FakeExtractor("fake", result)));
        });
        using var client = configured.CreateClient();

        using var response = await client.SendAsync(ReceiptRequest(space, member, includeManualFields: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<PurchaseViewProbe>();
        Assert.NotNull(view);
        Assert.Single(view!.Items);
        Assert.Equal("Ware", view.Items[0].Name);

        await using var scope = configured.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == view.Id);
        Assert.Equal(10m, purchase.SubtotalAmount);
        Assert.Equal(2m, purchase.DiscountAmount);
        Assert.Equal(.25m, purchase.DepositAmount);
        Assert.Equal(1m, purchase.ShippingAmount);
        Assert.Equal(-.01m, purchase.RoundingAmount);
        var discount = Assert.Single(await db.Set<PurchaseDiscount>().AsNoTracking().Where(x => x.PurchaseId == view.Id).ToListAsync());
        Assert.Equal("coupon", discount.Type);
        Assert.Equal(2m, discount.Amount);
        Assert.Equal("fake", discount.Source);
        Assert.Equal(1, await db.PurchaseItems.CountAsync(x => x.PurchaseId == view.Id));
    }

    [Fact]
    public async Task ReceiptScan_WithNoProvider_StaysCaptured()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, member) = await SeedMemberAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ReceiptRequest(space, member, includeManualFields: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<PurchaseViewProbe>();
        Assert.Equal("captured", view!.Status);
        Assert.Empty(view.Items);
    }

    [Fact]
    public async Task ReceiptScan_ExtractorThrows_StillCapturedNo500()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, member) = await SeedMemberAsync(factory);

        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ReceiptExtraction:Provider"] = "boom" }));
            b.ConfigureTestServices(s => s.AddSingleton<IReceiptExtractor>(new ThrowingExtractor("boom")));
        });
        using var client = configured.CreateClient();

        using var response = await client.SendAsync(ReceiptRequest(space, member, includeManualFields: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<PurchaseViewProbe>();
        Assert.Equal("captured", view!.Status);
        Assert.Empty(view.Items);
    }

    [Fact]
    public async Task ReceiptScan_FormValuesWinOverOcr()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, member) = await SeedMemberAsync(factory);

        var result = new ReceiptExtractionResult("fake", "OcrMerchant", new DateOnly(2026, 1, 1), null, 99m, null, null, null, [], 0.8m);
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ReceiptExtraction:Provider"] = "fake" }));
            b.ConfigureTestServices(s => s.AddSingleton<IReceiptExtractor>(new FakeExtractor("fake", result)));
        });
        using var client = configured.CreateClient();

        using var response = await client.SendAsync(ReceiptRequest(space, member, includeManualFields: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<PurchaseViewProbe>();
        Assert.Equal("Manual Shop", view!.Merchant);
        Assert.Equal(9.99m, view.TotalAmount);
    }

    private static async Task<(Guid Space, Guid Member)> SeedMemberAsync(BackendWebApplicationFactory factory)
    {
        var space = Guid.NewGuid();
        var member = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = member, EmailNormalized = $"{member:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "OCR", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "OCR Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member resolves to the read-only viewer template,
            // so grant editor (which carries purchases.manage) to reach the receipt-scan write handler.
            await CapabilityTestSeeding.GrantEditorAsync(db, space, member);
        });
        return (space, member);
    }

    private static HttpRequestMessage ReceiptRequest(Guid fullWorthSpaceId, Guid userId, bool includeManualFields)
    {
        var multipart = new MultipartFormDataContent();
        if (includeManualFields)
        {
            multipart.Add(new StringContent("Manual Shop"), "merchant");
            multipart.Add(new StringContent("2026-08-15"), "purchaseDate");
            multipart.Add(new StringContent("9.99"), "totalAmount");
        }
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new ByteArrayContent(PngBytes), "receipt", "receipt.png");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/purchases/receipt-scan?fullWorthSpaceId={fullWorthSpaceId:D}") { Content = multipart };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed class FakeExtractor(string provider, ReceiptExtractionResult result) : IReceiptExtractor
    {
        public string Provider => provider;
        public Task<ReceiptExtractionResult> ExtractAsync(ReceiptExtractionRequest request, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class ThrowingExtractor(string provider) : IReceiptExtractor
    {
        public string Provider => provider;
        public Task<ReceiptExtractionResult> ExtractAsync(ReceiptExtractionRequest request, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed record PurchaseViewProbe(Guid Id, string Merchant, decimal TotalAmount, string Status, List<ItemProbe> Items);
    private sealed record ItemProbe(string Name, decimal TotalPrice);
}
