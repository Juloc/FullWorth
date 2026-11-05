namespace FullWorth.Backend.Modules.Purchases.Extraction;

/// <summary>
/// One receipt is read more than once - the same page with different layouts - because each reading
/// makes different mistakes. The reading to keep is the one whose arithmetic agrees; among those, the
/// one most readings agree on. Readings that disagree with each other are never mixed line by line.
/// </summary>
internal static class ReceiptReadingSelector
{
    public static ReceiptExtractionResult Choose(IReadOnlyList<ReceiptExtractionResult> readings)
    {
        if (readings.Count == 0) throw new ArgumentException("At least one reading is required.", nameof(readings));

        var best = readings
            .GroupBy(Signature)
            .OrderByDescending(group => group.Max(reading => reading.Confidence))
            .ThenByDescending(group => group.Count())
            .First();
        return best.OrderByDescending(reading => reading.Confidence).First();
    }

    /// <summary>What two readings must share to count as the same reading: the total and every line price.</summary>
    private static string Signature(ReceiptExtractionResult reading) =>
        $"{reading.Total}|{string.Join(',', reading.Items.Select(item => item.TotalPrice).OrderBy(price => price))}";
}
