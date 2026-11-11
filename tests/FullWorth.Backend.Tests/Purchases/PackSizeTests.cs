using FullWorth.Backend.Modules.Purchases;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PackSizeTests
{
    // Namen und Mengenangaben, wie sie auf den Belegen der Benutzer standen.
    [Theory]
    [InlineData("Knoblauch 200 g", "Knoblauch", null, 200, "g")]
    [InlineData("Brokkoli 1 kg", "Brokkoli", null, 1, "kg")]
    [InlineData("Spülmittel Himbeere 1 l", "Spülmittel Himbeere", null, 1, "l")]
    [InlineData("Tempeh natur, tiefgekühlt 394 g", "Tempeh natur, tiefgekühlt", null, 394, "g")]
    [InlineData("Eier, Bodenhaltung, 10er", "Eier, Bodenhaltung", null, 10, "piece")]
    [InlineData("Eier 6 Stück", "Eier", null, 6, "piece")]
    [InlineData("Joghurt 5x55g", "Joghurt", 5, 55, "g")]
    [InlineData("Cola 6 x 1,5 l", "Cola", 6, 1.5, "l")]
    [InlineData("Radler 33 cl", "Radler", null, 330, "ml")]
    [InlineData("6er Chicken McNuggets", "Chicken McNuggets", null, 6, "piece")]
    [InlineData("Passierte Tomaten 500g", "Passierte Tomaten", null, 500, "g")]
    public void A_pack_at_the_end_of_the_name_moves_to_the_item(string name, string rest, int? count, double size, string unit)
    {
        var reading = PackSize.FromName(name);

        Assert.NotNull(reading);
        Assert.Equal(rest, reading!.Name);
        Assert.Equal((decimal?)count, reading.Count);
        Assert.Equal((decimal)size, reading.Quantity);
        Assert.Equal(unit, reading.Unit);
    }

    // Zahlen, die keine Packung sind, bleiben im Namen.
    [Theory]
    [InlineData("Super 95")]
    [InlineData("H-Milch 3,5 % laktosefrei")]
    [InlineData("Pizza")]
    [InlineData("500 g")]
    [InlineData("Kokos 200 gramm Riegel")]
    [InlineData("Hähnchenbrustfilet")]
    public void Numbers_that_are_not_a_pack_stay_in_the_name(string name)
    {
        Assert.Null(PackSize.FromName(name));
    }

    [Theory]
    [InlineData("500 g", null, 500, "g")]
    [InlineData("0,5 l", null, 0.5, "l")]
    [InlineData("2 × 100 g", 2, 100, "g")]
    [InlineData("20 × 30 g", 20, 30, "g")]
    [InlineData("50 stück", null, 50, "piece")]
    public void A_quantity_that_is_only_a_pack_is_read_as_one(string text, int? count, double size, string unit)
    {
        var reading = PackSize.FromQuantityUnit(text);

        Assert.NotNull(reading);
        Assert.Null(reading!.Name);
        Assert.Equal((decimal?)count, reading.Count);
        Assert.Equal((decimal)size, reading.Quantity);
        Assert.Equal(unit, reading.Unit);
    }

    [Theory]
    [InlineData("stk")]
    [InlineData("kg")]
    [InlineData("packung")]
    [InlineData("Packung 500 g")]
    [InlineData(null)]
    public void A_plain_unit_is_not_a_pack(string? text)
    {
        Assert.Null(PackSize.FromQuantityUnit(text));
    }

    [Fact]
    public void A_pack_that_is_already_set_is_left_alone()
    {
        var resolved = PackSize.Resolve("Knoblauch 200 g", "piece", 150m, "g", null);

        Assert.Equal("Knoblauch 200 g", resolved.Name);
        Assert.Equal(150m, resolved.Quantity);
    }

    [Fact]
    public void The_quantity_unit_wins_over_the_name_and_becomes_a_piece_count()
    {
        var resolved = PackSize.Resolve("Mozzarella", "125 g", null, null, null);

        Assert.Equal("Mozzarella", resolved.Name);
        Assert.Equal("piece", resolved.QuantityUnit);
        Assert.Equal(125m, resolved.Quantity);
        Assert.Equal("g", resolved.Unit);
    }
}
