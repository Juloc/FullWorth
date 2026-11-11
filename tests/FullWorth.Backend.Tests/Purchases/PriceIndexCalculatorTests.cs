using FullWorth.Backend.Modules.Purchases;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PriceIndexCalculatorTests
{
    private static PriceObservation Obs(string merchant, string name, int year, int month, decimal price, decimal spend = 10m) =>
        new(PriceIndexCalculator.ItemKey(merchant, null, name), null, new DateOnly(year, month, 15), price, spend);

    private static IEnumerable<PriceObservation> Basket(int year, int month, decimal factor) =>
    [
        Obs("REWE Markt GmbH", "Milch 1L", year, month, 1.00m * factor),
        Obs("REWE Markt GmbH", "Butter", year, month, 2.00m * factor),
        Obs("PENNY-MARKT GmbH", "Pizza", year, month, 3.00m * factor),
        Obs("ALDI SÜD", "Nudeln", year, month, 1.50m * factor)
    ];

    [Fact]
    public void A_ten_percent_rise_between_quarters_is_an_index_of_110()
    {
        var points = PriceIndexCalculator.Chain(Basket(2024, 2, 1m).Concat(Basket(2024, 5, 1.10m)));

        Assert.Equal(["2024-Q1", "2024-Q2"], points.Select(p => p.Quarter));
        Assert.Equal(100m, points[0].Index);
        Assert.Equal(110m, points[1].Index);
        Assert.Equal(4, points[1].MatchedItems);
    }

    [Fact]
    public void The_same_name_in_another_shop_is_not_the_same_item()
    {
        var points = PriceIndexCalculator.Chain(
            Basket(2024, 2, 1m).Concat(
            [
                Obs("LIDL", "Milch 1L", 2024, 5, 9m),
                Obs("REWE Markt GmbH", "Butter", 2024, 5, 2.00m),
                Obs("PENNY-MARKT GmbH", "Pizza", 2024, 5, 3.00m),
                Obs("ALDI SÜD", "Nudeln", 2024, 5, 1.50m)
            ]));

        // Nur drei Artikel sind vergleichbar - und die sind gleich geblieben, die Milch zaehlt nicht mit.
        Assert.Equal(100m, points[^1].Index);
    }

    [Fact]
    public void A_quarter_with_too_few_comparable_items_gets_no_point()
    {
        var points = PriceIndexCalculator.Chain(
            Basket(2024, 2, 1m).Concat([Obs("REWE Markt GmbH", "Milch 1L", 2024, 5, 2m), Obs("REWE Markt GmbH", "Butter", 2024, 5, 4m)]));

        Assert.Empty(points);
    }

    [Fact]
    public void A_misread_price_is_left_out_instead_of_becoming_inflation()
    {
        var points = PriceIndexCalculator.Chain(
            Basket(2024, 2, 1m).Concat(Basket(2024, 5, 1m).Select(o => o.ItemKey.EndsWith("butter") ? o with { UnitPrice = 200m } : o)));

        Assert.Equal(100m, points[1].Index);
        Assert.Equal(3, points[1].MatchedItems);
    }

    [Fact]
    public void Quarters_chain_so_the_second_step_builds_on_the_first()
    {
        var points = PriceIndexCalculator.Chain(Basket(2024, 2, 1m).Concat(Basket(2024, 5, 1.10m)).Concat(Basket(2024, 8, 1.21m)));

        Assert.Equal(121m, points[2].Index);
    }
}
