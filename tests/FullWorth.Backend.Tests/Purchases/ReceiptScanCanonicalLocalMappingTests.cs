using System.Collections;
using System.Reflection;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Purchases.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ReceiptScanCanonicalLocalMappingTests
{
    [Fact]
    public void Local_mapping_keeps_structured_item_discount_through_overlap_dedupe()
    {
        var extraction = new ReceiptExtractionService(
            [new FakeExtractor()],
            Options.Create(new ReceiptExtractionOptions { Provider = "tesseract" }));
        var processor = new ReceiptScanQueueProcessor(
            null!, null!, null!, extraction, null!,
            Options.Create(new PurchaseStorageOptions()),
            NullLogger<ReceiptScanQueueProcessor>.Instance);

        var first = new ReceiptExtractionResult(
            "tesseract", "Markt", new DateOnly(2026, 8, 31), "EUR", null, null, null, null,
            [
                new ReceiptLineItem("Cola", 1m, 1.99m, 1.99m, null, .95m, "piece", 2.49m, .50m, "App Rabatt", .25m, "product"),
                new ReceiptLineItem("Chips", 1m, 2m, 2m, null, .95m)
            ],
            .9m);
        var second = first with
        {
            Total = 5.23m,
            Discounts = .50m,
            Deposits = .25m,
            Subtotal = 4.49m,
            Rounding = -.01m,
            Shipping = 1m,
            StructuredDiscounts =
            [
                new ReceiptDiscount("loyalty", "App Rabatt", .50m, Confidence: .95m, ItemIndex: 0)
            ]
        };

        var prepared = InvokeBuildLocal(processor, [first, second]);
        var request = Assert.IsType<PurchaseExtractionRequest>(prepared.GetType().GetProperty("Request")!.GetValue(prepared));
        var provenance = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlySet<int>>>(prepared.GetType().GetProperty("ItemSourceIndexes")!.GetValue(prepared));

        Assert.True(request.AmountsAreCanonical);
        Assert.Equal(4.49m, request.SubtotalAmount);
        Assert.Equal(.50m, request.DiscountAmount);
        Assert.Equal(.25m, request.DepositAmount);
        Assert.Equal(1m, request.ShippingAmount);
        Assert.Equal(-.01m, request.RoundingAmount);
        Assert.Equal(5.23m, request.TotalAmount);
        Assert.Equal(2, request.Items.Count);
        Assert.DoesNotContain(request.Items, item => item.LineType is "discount" or "deposit");

        var cola = request.Items[0];
        Assert.Equal("Cola", cola.Name);
        Assert.Equal(1.99m, cola.TotalPrice);
        Assert.Equal(2.49m, cola.OriginalUnitPrice);
        Assert.Equal(.50m, cola.DiscountAmount);
        Assert.Equal(.25m, cola.DepositAmount);
        Assert.Equal(new[] { 0, 1 }, provenance[0].OrderBy(x => x).ToArray());

        var discount = Assert.Single(request.Discounts!);
        Assert.Equal("loyalty", discount.Type);
        Assert.Equal(.50m, discount.Amount);
        Assert.Equal(0, discount.ItemIndex);
        Assert.Equal("tesseract", request.DiscountSource);
    }

    [Fact]
    public void Local_mapping_creates_basket_remainder_without_fake_item()
    {
        var extraction = new ReceiptExtractionService(
            [new FakeExtractor()],
            Options.Create(new ReceiptExtractionOptions { Provider = "tesseract" }));
        var processor = new ReceiptScanQueueProcessor(
            null!, null!, null!, extraction, null!,
            Options.Create(new PurchaseStorageOptions()),
            NullLogger<ReceiptScanQueueProcessor>.Instance);
        var result = new ReceiptExtractionResult(
            "tesseract", "Markt", null, "EUR", 8m, 2m, null, null,
            [new ReceiptLineItem("Ware", 1m, 10m, 10m, null, .8m)], .8m);

        var prepared = InvokeBuildLocal(processor, [result]);
        var request = (PurchaseExtractionRequest)prepared.GetType().GetProperty("Request")!.GetValue(prepared)!;

        Assert.Single(request.Items);
        Assert.Equal("product", request.Items[0].LineType);
        var discount = Assert.Single(request.Discounts!);
        Assert.Null(discount.ItemIndex);
        Assert.Equal(2m, discount.Amount);
        Assert.Equal("other", discount.Type);
    }

    private static object InvokeBuildLocal(ReceiptScanQueueProcessor processor, IReadOnlyList<ReceiptExtractionResult> results)
    {
        var nested = typeof(ReceiptScanQueueProcessor).GetNestedType("LocalSourceExtraction", BindingFlags.NonPublic)!;
        var listType = typeof(List<>).MakeGenericType(nested);
        var list = (IList)Activator.CreateInstance(listType)!;
        for (var index = 0; index < results.Count; index++)
        {
            var source = new ReceiptScanSourceRow
            {
                Id = Guid.NewGuid(),
                ReceiptScanJobId = Guid.NewGuid(),
                SortOrder = index,
                SourceType = "image",
                OriginalFileName = $"{index}.png",
                MimeType = "image/png",
                StoragePath = $"{index}.png",
                Fingerprint = $"f-{index}",
                SizeBytes = 1
            };
            list.Add(Activator.CreateInstance(nested, index, source, results[index])!);
        }

        var method = typeof(ReceiptScanQueueProcessor).GetMethod("BuildLocalExtraction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return method.Invoke(processor, [list, "EUR", new Dictionary<string, Guid>(), results.Count])!;
    }

    private sealed class FakeExtractor : IReceiptExtractor
    {
        public string Provider => "tesseract";
        public Task<ReceiptExtractionResult> ExtractAsync(ReceiptExtractionRequest request, CancellationToken ct) =>
            Task.FromResult(ReceiptExtractionResult.Empty(Provider));
    }
}
