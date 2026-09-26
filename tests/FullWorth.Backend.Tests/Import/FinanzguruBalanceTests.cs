using FullWorth.Backend.Modules.Import;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Der Kontostand aus der Spalte "Kontostand" des Finanzguru-Exports - nachgerechnet, nicht geglaubt.
/// Die Zeilen stehen neueste zuerst, wie im echten Export; die Zeilennummer ist die Reihenfolge der Datei.
/// </summary>
public sealed class FinanzguruBalanceTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);

    private static FinanzguruRow Row(int number, string date, decimal amount, string? balance) => new(
        number, DateOnly.Parse(date), "DE00", "Girokonto", amount, "EUR", "Gegenseite", null, null, null,
        null, null, false, $"id-{number}", null, null,
        new Dictionary<string, string?> { ["Kontostand"] = balance });

    [Fact]
    public void TheNewestBalanceThatAddsUpIsTheAnchor()
    {
        var anchor = FinanzguruBalance.Anchor(
            [Row(2, "2026-09-10", -20m, "80.00"), Row(3, "2026-09-05", 100m, "100.00")], Today);

        Assert.Equal(80m, anchor!.Amount);
        Assert.Equal(new DateOnly(2026, 9, 10), anchor.AsOf);
        Assert.Equal("EUR", anchor.Currency);
    }

    /// <summary>Eine fehlende Buchung dazwischen - 100 minus 20 ist nicht 70.</summary>
    [Fact]
    public void ABalanceThatDoesNotAddUpIsNoAnchor() =>
        Assert.Null(FinanzguruBalance.Anchor(
            [Row(2, "2026-09-10", -20m, "70.00"), Row(3, "2026-09-05", 100m, "100.00")], Today));

    /// <summary>Eine vorgemerkte Buchung nach heute traegt einen Stand, den das Konto noch nicht hat.</summary>
    [Fact]
    public void ABookingAfterTodayDoesNotSetTheBalance()
    {
        var anchor = FinanzguruBalance.Anchor(
            [Row(2, "2026-10-06", -5m, "75.00"), Row(3, "2026-09-10", -20m, "80.00"), Row(4, "2026-09-05", 100m, "100.00")], Today);

        Assert.Equal(80m, anchor!.Amount);
        Assert.Equal(new DateOnly(2026, 9, 10), anchor.AsOf);
    }

    /// <summary>Zwei Buchungen an einem Tag: die weiter oben in der Datei ist die spaetere.</summary>
    [Fact]
    public void OnOneDayTheRowHigherInTheFileIsTheLater()
    {
        var anchor = FinanzguruBalance.Anchor(
            [Row(2, "2026-09-10", -30m, "50.00"), Row(3, "2026-09-10", -20m, "80.00"), Row(4, "2026-09-05", 100m, "100.00")], Today);

        Assert.Equal(50m, anchor!.Amount);
    }

    [Fact]
    public void ASingleBookingHasNothingThatContradictsIt() =>
        Assert.Equal(42.5m, FinanzguruBalance.Anchor([Row(2, "2026-09-10", -7.5m, "42.50")], Today)!.Amount);

    [Fact]
    public void WithoutTheColumnThereIsNoAnchor() =>
        Assert.Null(FinanzguruBalance.Anchor([Row(2, "2026-09-10", -20m, null), Row(3, "2026-09-05", 100m, null)], Today));

    /// <summary>Der echte Export schreibt einen Punkt; eine deutsche Schreibweise liest sich genauso.</summary>
    [Fact]
    public void BothNotationsAreRead() =>
        Assert.Equal(1234.56m, FinanzguruBalance.Anchor(
            [Row(2, "2026-09-10", 34.56m, "1.234,56"), Row(3, "2026-09-05", 100m, "1200.00")], Today)!.Amount);
}
