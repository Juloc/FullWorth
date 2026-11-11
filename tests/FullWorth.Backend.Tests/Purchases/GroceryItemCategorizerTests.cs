using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Purchases;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class GroceryItemCategorizerTests
{
    // Namen, wie sie auf Penny-, Kaufland- und REWE-Bons standen - samt den Faellen, die ein Wort in zwei Gruppen stecken haetten.
    [Theory]
    [InlineData("Eier aus Bodenhaltung", "food.groceries.dairy")]
    [InlineData("Eier, Bodenhaltung", "food.groceries.dairy")]
    [InlineData("Bananen", "food.groceries.produce")]
    [InlineData("Hähnchenbrustfilet", "food.groceries.meat")]
    [InlineData("Rinderhackfleisch", "food.groceries.meat")]
    [InlineData("Kochhinterschinken", "food.groceries.meat")]
    [InlineData("Lauchzwiebeln", "food.groceries.produce")]
    [InlineData("Rispentomaten", "food.groceries.produce")]
    [InlineData("Eisbergsalat", "food.groceries.produce")]
    [InlineData("Knoblauch", "food.groceries.produce")]
    [InlineData("Mini-Pak-Choi", "food.groceries.produce")]
    [InlineData("Rapsöl", "food.groceries.spices")]
    [InlineData("Jodsalz", "food.groceries.spices")]
    [InlineData("Sambal Oelek", "food.groceries.spices")]
    [InlineData("Röstzwiebeln", "food.groceries.spices")]
    [InlineData("Frischkäse Kräuter", "food.groceries.dairy")]
    [InlineData("Speisequark", "food.groceries.dairy")]
    [InlineData("Mozzarella", "food.groceries.dairy")]
    [InlineData("H-Milch 3,5 % laktosefrei", "food.groceries.dairy")]
    [InlineData("Creme zum Kochen", "food.groceries.dairy")]
    [InlineData("Käseaufschnitt leicht", "food.groceries.dairy")]
    [InlineData("Butter", "food.groceries.dairy")]
    [InlineData("Kokosnussmilch", "food.groceries.staples")]
    [InlineData("Passierte Tomaten", "food.groceries.staples")]
    [InlineData("Instantnudeln Huhn Spezial", "food.groceries.staples")]
    [InlineData("Schupfnudeln", "food.groceries.staples")]
    [InlineData("Weltmeisterbrot", "food.groceries.bread")]
    [InlineData("Laugenstange", "food.groceries.bread")]
    [InlineData("Knoblauchbaguette", "food.groceries.bread")]
    [InlineData("Pizza Margherita", "food.groceries.frozen")]
    [InlineData("Kaisergemüse", "food.groceries.frozen")]
    [InlineData("Tempeh natur, tiefgekühlt", "food.groceries.vegan")]
    [InlineData("Sojadrink", "food.groceries.vegan")]
    [InlineData("Sahnepudding Vanille", "food.groceries.sweets")]
    [InlineData("Sahnepudding Schokolade", "food.groceries.sweets")]
    [InlineData("Alpenmilch Schokolade", "food.groceries.chocolate")]
    [InlineData("Chips Original", "food.groceries.snacks")]
    [InlineData("Sour Cream Chips", "food.groceries.snacks")]
    [InlineData("Party Mix Minis", "food.groceries.snacks")]
    [InlineData("Haferflocken", "food.groceries.breakfast")]
    [InlineData("Knuspermüsli", "food.groceries.breakfast")]
    [InlineData("Thunfisch pikant", "food.groceries.fish")]
    [InlineData("Fischstäbchen XXL", "food.groceries.frozen")]
    [InlineData("Coca-Cola Zero", "food.groceries.drinks")]
    [InlineData("Orangensaft", "food.groceries.drinks")]
    [InlineData("Wodka", "food.groceries.spirits")]
    [InlineData("Rotwein trocken", "food.groceries.wine")]
    [InlineData("Pils", "food.groceries.beer")]
    [InlineData("Toilettenpapier", "shopping.household")]
    [InlineData("Spülmittel Himbeere", "shopping.household")]
    [InlineData("Zahncreme", "shopping.drugstore")]
    [InlineData("Shampoo", "shopping.drugstore")]
    public void A_name_lands_in_the_group_it_belongs_to(string name, string key)
    {
        Assert.Equal(key, GroceryItemCategorizer.KeyFor(name));
    }

    // Ein Wort, das nur zufaellig in einem anderen steckt, zaehlt nicht.
    [Theory]
    [InlineData("Schweinefilet", "food.groceries.meat")]
    [InlineData("Eisbergsalat", "food.groceries.produce")]
    [InlineData("Suppengemüse", "food.groceries.produce")]
    [InlineData("Salzstangen", "food.groceries.snacks")]
    [InlineData("Erdnussbutter", "food.groceries.breakfast")]
    public void A_word_inside_another_word_does_not_decide(string name, string key)
    {
        Assert.Equal(key, GroceryItemCategorizer.KeyFor(name));
    }

    [Theory]
    [InlineData("Super 95")]
    [InlineData("Leergutrückgabe Getränke")]
    [InlineData("Frank Rosin Hot")]
    [InlineData("")]
    [InlineData(null)]
    public void What_matches_nothing_stays_unsorted(string? name)
    {
        Assert.Null(GroceryItemCategorizer.KeyFor(name));
    }

    [Fact]
    public void Every_key_it_hands_out_is_a_standard_category()
    {
        var known = FullWorthSeeder.DefaultCategoryKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(GroceryItemCategorizer.Keys, key => Assert.Contains(key, known));
    }
}
