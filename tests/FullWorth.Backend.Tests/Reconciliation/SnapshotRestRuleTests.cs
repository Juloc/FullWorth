using FullWorth.Backend.Modules.Reconciliation;

namespace FullWorth.Backend.Tests.Reconciliation;

/// <summary>
/// Die reine Rest-Regel, isoliert von jeder Datenbank: Buchungen erklaeren einen von FinTS gemeldeten
/// Bestand teilweise, ganz, oder mehr als ganz. Siehe SnapshotRestRule fuer die Herleitung.
/// </summary>
public sealed class SnapshotRestRuleTests
{
    [Fact]
    public void Positive_rest_keeps_the_snapshot_row_with_only_the_remainder()
    {
        var result = SnapshotRestRule.Evaluate(snapshotQuantity: 10m, otherTradesQuantity: 4m);

        Assert.Equal(SnapshotRestAction.SetRemainder, result.Action);
        Assert.Equal(6m, result.Remainder);
        Assert.False(result.Overexplained);
    }

    [Fact]
    public void Exact_match_deletes_the_snapshot_row_without_flagging_a_discrepancy()
    {
        var result = SnapshotRestRule.Evaluate(snapshotQuantity: 10m, otherTradesQuantity: 10m);

        Assert.Equal(SnapshotRestAction.Delete, result.Action);
        Assert.False(result.Overexplained);
    }

    [Fact]
    public void Overexplained_bookings_delete_the_snapshot_row_and_flag_the_discrepancy()
    {
        var result = SnapshotRestRule.Evaluate(snapshotQuantity: 10m, otherTradesQuantity: 14m);

        Assert.Equal(SnapshotRestAction.Delete, result.Action);
        Assert.True(result.Overexplained);
    }

    [Fact]
    public void No_other_trades_keeps_the_full_snapshot_quantity()
    {
        var result = SnapshotRestRule.Evaluate(snapshotQuantity: 10m, otherTradesQuantity: 0m);

        Assert.Equal(SnapshotRestAction.SetRemainder, result.Action);
        Assert.Equal(10m, result.Remainder);
        Assert.False(result.Overexplained);
    }
}
