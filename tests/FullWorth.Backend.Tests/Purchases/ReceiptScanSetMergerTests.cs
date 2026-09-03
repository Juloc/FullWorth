using FullWorth.Backend.Modules.Purchases.Extraction;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class ReceiptScanSetMergerTests
{
    [Fact]
    public void MergeRemovesOnlyConfidentMultiLineBoundaryOverlap()
    {
        var first = Result(
            total: null,
            Item("Apfel", 1.20m),
            Item("Milch", 1.10m),
            Item("Brot", 2.50m));
        var second = Result(
            total: 7.40m,
            Item("Milch", 1.10m),
            Item("Brot", 2.50m),
            Item("Kaffee", 2.60m));

        var merged = ReceiptScanSetMerger.Merge([
            new ReceiptSourceExtraction(0, first),
            new ReceiptSourceExtraction(1, second)
        ]);

        Assert.Equal(new[] { "Apfel", "Milch", "Brot", "Kaffee" }, merged.Items.Select(x => x.Item.Name).ToArray());
        Assert.Equal(new[] { 0, 0, 0, 1 }, merged.Items.Select(x => x.SourceOrder).ToArray());
        Assert.Equal(7.40m, merged.Total);
        Assert.Contains(merged.Warnings, warning => warning.Contains("removed 2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MergeKeepsSingleMatchingBoundaryLineForReview()
    {
        var first = Result(total: null, Item("Milch", 1.10m));
        var second = Result(total: 2.20m, Item("Milch", 1.10m));

        var merged = ReceiptScanSetMerger.Merge([
            new ReceiptSourceExtraction(0, first),
            new ReceiptSourceExtraction(1, second)
        ]);

        Assert.Equal(2, merged.Items.Count);
        Assert.All(merged.Items, row => Assert.Equal("Milch", row.Item.Name));
        Assert.Contains(merged.Warnings, warning => warning.Contains("one-line overlap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MergePrefersLastDetectedReceiptTotalsInsteadOfSummingPages()
    {
        var first = new ReceiptExtractionResult(
            "tesseract", "REWE", new DateOnly(2026, 8, 31), "EUR",
            12m, 1m, null, null, [Item("A", 5m)], .8m);
        var second = new ReceiptExtractionResult(
            "tesseract", null, null, null,
            20m, 2m, 0.25m, 3.19m, [Item("B", 15m)], .9m);

        var merged = ReceiptScanSetMerger.Merge([
            new ReceiptSourceExtraction(0, first),
            new ReceiptSourceExtraction(1, second)
        ]);

        Assert.Equal("REWE", merged.Merchant);
        Assert.Equal(new DateOnly(2026, 8, 31), merged.PurchaseDate);
        Assert.Equal("EUR", merged.Currency);
        Assert.Equal(20m, merged.Total);
        Assert.Equal(2m, merged.Discounts);
        Assert.Equal(0.25m, merged.Deposits);
        Assert.Equal(3.19m, merged.Taxes);
        Assert.Equal(.85m, merged.Confidence);
    }

    [Fact]
    public void FindBoundaryOverlapUsesAmountsSoSameProductAtDifferentPriceIsNotDeleted()
    {
        var previous = new[] { Item("Cola", 1.49m), Item("Wasser", .79m) };
        var next = new[] { Item("Cola", 1.29m), Item("Wasser", .79m) };

        Assert.Equal(0, ReceiptScanSetMerger.FindBoundaryOverlap(previous, next));
    }

    private static ReceiptExtractionResult Result(decimal? total, params ReceiptLineItem[] items) =>
        new("tesseract", "Shop", null, "EUR", total, null, null, null, items, .9m);

    private static ReceiptLineItem Item(string name, decimal total) =>
        new(name, 1m, total, total, null, .95m);
}
