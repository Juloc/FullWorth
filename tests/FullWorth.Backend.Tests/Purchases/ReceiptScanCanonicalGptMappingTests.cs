using System.Reflection;
using FullWorth.Backend.Modules.Purchases;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ReceiptScanCanonicalGptMappingTests
{
    [Fact]
    public void Gpt_mapping_keeps_discount_assignment_through_overlap_dedupe()
    {
        var processor = new ReceiptScanQueueProcessor(
            null!, null!, null!, null!, null!,
            Options.Create(new PurchaseStorageOptions()),
            NullLogger<ReceiptScanQueueProcessor>.Instance);

        var envelope = new CodexReceiptScanEnvelope
        {
            Success = true,
            RequestId = "canonical-test",
            Result = new CodexReceiptResult
            {
                Merchant = new CodexMerchant { Name = "Testmarkt" },
                Receipt = new CodexReceiptMeta { Date = "2026-08-31", Currency = "EUR" },
                Totals = new CodexReceiptTotals
                {
                    Subtotal = 4.49m,
                    Discounts = .50m,
                    Deposits = .25m,
                    Rounding = -.01m,
                    Total = 4.23m
                },
                Items =
                [
                    Item("Cola", 1.99m, 2.49m, .50m, .25m, 0),
                    Item("Chips", 2.00m, null, null, null, 0),
                    Item("Cola", 1.99m, 2.49m, .50m, .25m, 1),
                    Item("Chips", 2.00m, null, null, null, 1)
                ],
                Discounts =
                [
                    new CodexReceiptDiscount
                    {
                        Type = "loyalty",
                        Label = "App Rabatt",
                        Amount = .50m,
                        ItemIndex = 2,
                        Confidence = .95m,
                        SourceIndexes = [1]
                    }
                ],
                Confidence = .97m
            }
        };

        var method = typeof(ReceiptScanQueueProcessor).GetMethod(
            "BuildGptExtraction",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var prepared = method!.Invoke(processor, [envelope, "EUR", new Dictionary<string, Guid>(), 2]);
        Assert.NotNull(prepared);

        var request = Assert.IsType<PurchaseExtractionRequest>(
            prepared!.GetType().GetProperty("Request")!.GetValue(prepared));
        var provenance = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlySet<int>>>(
            prepared.GetType().GetProperty("ItemSourceIndexes")!.GetValue(prepared));

        Assert.True(request.AmountsAreCanonical);
        Assert.Equal(-.01m, request.RoundingAmount);
        Assert.Equal(2, request.Items.Count);
        Assert.DoesNotContain(request.Items, item => item.LineType is "discount" or "deposit");

        var cola = request.Items[0];
        Assert.Equal("Cola", cola.Name);
        Assert.Equal(1.99m, cola.TotalPrice);
        Assert.Equal(2.49m, cola.OriginalUnitPrice);
        Assert.Equal(.50m, cola.DiscountAmount);
        Assert.Equal("App Rabatt", cola.DiscountLabel);
        Assert.Equal(.25m, cola.DepositAmount);
        Assert.Equal(new[] { 0, 1 }, provenance[0].OrderBy(x => x).ToArray());

        var discount = Assert.Single(request.Discounts!);
        Assert.Equal("loyalty", discount.Type);
        Assert.Equal(.50m, discount.Amount);
        Assert.Equal(0, discount.ItemIndex);
        Assert.Equal("codex", discount.Source);
    }

    [Fact]
    public void Gpt_mapping_does_not_turn_basket_discount_into_article_line()
    {
        var processor = new ReceiptScanQueueProcessor(
            null!, null!, null!, null!, null!,
            Options.Create(new PurchaseStorageOptions()),
            NullLogger<ReceiptScanQueueProcessor>.Instance);
        var envelope = new CodexReceiptScanEnvelope
        {
            Success = true,
            Result = new CodexReceiptResult
            {
                Merchant = new CodexMerchant { Name = "Testmarkt" },
                Receipt = new CodexReceiptMeta { Currency = "EUR" },
                Totals = new CodexReceiptTotals { Discounts = 2m, Total = 8m },
                Items = [Item("Produkt", 10m, null, null, null, 0)],
                Discounts =
                [
                    new CodexReceiptDiscount
                    {
                        Type = "coupon",
                        Label = "Coupon",
                        Amount = 2m,
                        ItemIndex = null,
                        Confidence = .9m,
                        SourceIndexes = [0]
                    }
                ]
            }
        };

        var method = typeof(ReceiptScanQueueProcessor).GetMethod(
            "BuildGptExtraction",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var prepared = method.Invoke(processor, [envelope, "EUR", new Dictionary<string, Guid>(), 1])!;
        var request = (PurchaseExtractionRequest)prepared.GetType().GetProperty("Request")!.GetValue(prepared)!;

        Assert.Single(request.Items);
        Assert.Equal(10m, request.Items[0].TotalPrice);
        Assert.Equal("product", request.Items[0].LineType);
        var discount = Assert.Single(request.Discounts!);
        Assert.Null(discount.ItemIndex);
        Assert.Equal("coupon", discount.Type);
        Assert.Equal(8m, request.TotalAmount);
    }

    private static CodexReceiptItem Item(
        string name,
        decimal total,
        decimal? originalUnitPrice,
        decimal? discount,
        decimal? deposit,
        int sourceIndex) => new()
        {
            RawName = name,
            Name = name,
            Quantity = 1m,
            Unit = "piece",
            UnitPrice = total,
            OriginalUnitPrice = originalUnitPrice,
            TotalPrice = total,
            DiscountAmount = discount,
            DiscountLabel = discount.HasValue ? "App Rabatt" : null,
            Deposit = deposit,
            Confidence = .95m,
            SourceIndexes = [sourceIndex]
        };
}
