using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Ordnet eine Position vom Supermarktbon nach ihrem Namen einer Kategorie aus dem Lebensmittel-Baum zu
/// ("Hähnchenbrustfilet" -> Fleisch &amp; Wurst). Das Ergebnis ist ein Kategorieschluessel, kein Name: der
/// Benutzer darf umbenennen und umhaengen.
///
/// Die Reihenfolge ist die Logik. Das Spezielle steht vor dem Allgemeinen, weil ein Wort oft in mehreren
/// Gruppen stecken koennte: "Sahnepudding" ist ein Dessert und keine Sahne, "Kokosmilch" eine Konserve und
/// keine Milch, "Sojadrink" vegan und kein Getränk, "Spülmittel Himbeere" Haushalt und kein Obst.
///
/// Schreibweise der Woerter: normal = Wortanfang ("hähnchen" findet "Hähnchenbrust"), "*" = irgendwo im
/// Wort ("*käse" findet "Frischkäse"), "$" = ganzes Wort ("ei$" findet nicht "Eisbergsalat").
/// </summary>
public static partial class GroceryItemCategorizer
{
    private static readonly (string Key, string[] Words)[] Groups =
    [
        ("shopping.household", ["toilettenpapier", "klopapier", "küchenrolle", "allzweck", "spülmittel", "spülmaschinen", "waschmittel", "weichspüler", "müllbeutel", "müllsack", "frischhaltefolie", "alufolie", "backpapier", "gefrierbeutel", "*reiniger", "*kohle", "putz", "schwamm", "spültuch", "batterie", "kerze", "grillfackel", "grillanzünder", "bastelbedarf", "zeichenbedarf", "schreibwaren"]),
        ("shopping.drugstore", ["shampoo", "duschgel", "zahncreme", "zahnpasta", "zahnbürste", "waschlotion", "deo$", "deodorant", "seife", "handcreme", "rasier", "tampon", "windel", "feuchttücher", "wattepad", "haarspray", "sonnencreme", "taschentücher", "pflaster", "mundspül"]),

        ("food.groceries.spirits", ["wodka", "vodka", "whisky", "whiskey", "gin$", "rum$", "*likör", "liqueur", "korn$", "tequila", "jägermeister", "aperol", "obstler", "brandy", "cognac", "schnaps", "baileys", "aperitif"]),
        ("food.groceries.wine", ["wein$", "rotwein", "weißwein", "weisswein", "weinschorle", "rosé", "sekt", "prosecco", "champagner", "secco", "glühwein", "riesling", "merlot", "chardonnay", "lambrusco"]),
        ("food.groceries.beer", ["bier$", "*bier$", "pils", "weizen$", "helles", "radler", "kölsch", "malzbier", "craft beer"]),

        ("food.groceries.coffee", ["kaffee", "espresso", "kaffeebohnen", "kaffeepads", "tee$", "teebeutel", "kräutertee", "früchtetee", "grüntee", "schwarztee", "pfefferminztee", "kamillentee", "chai", "kakao"]),
        ("food.groceries.drinks", ["wasser$", "*mineralwasser", "sprudel", "cola", "fanta", "sprite", "limo", "*saft", "schorle", "eistee", "energy", "red bull", "monster", "tonic", "ginger ale", "almdudler", "spezi$", "getränk", "smoothie", "kokoswasser", "club mate"]),

        ("food.groceries.spices", ["*öl$", "essig", "*salz$", "pfeffer", "gewürz", "curry", "paprikapulver", "zimt", "senf", "ketchup", "mayonnaise", "majo", "*soße", "*sauce", "sambal", "*dressing", "bouillon", "*brühe", "röstzwiebel", "backpulver", "vanillezucker"]),
        ("food.groceries.vegan", ["tofu", "tempeh", "seitan", "soja", "haferdrink", "hafer drink", "hafermilch", "mandelmilch", "reisdrink", "veggie", "vegan", "vegetarisch", "falafel", "pflanzlich"]),
        ("food.groceries.frozen", ["tiefgekühlt", "tiefkühl", "pizza", "pommes", "curly fries", "fischstäbchen", "*nuggets", "kaisergemüse", "rahmspinat", "blattspinat", "gemüsepfanne"]),
        ("food.groceries.breakfast", ["*müsli", "haferflocken", "cornflakes", "cerealien", "marmelade", "konfitüre", "honig", "nutella", "nuss-nougat", "brotaufstrich", "erdnussbutter", "aufstrich", "porridge", "granola"]),
        ("food.groceries.snacks", ["*chips", "chipsi", "flips", "erdnüsse", "erdnuss", "cracker", "salzstangen", "popcorn", "party mix", "knabber", "nachos", "snack", "pringles", "studentenfutter", "sour cream"]),
        ("food.groceries.sweets", ["gummi", "haribo", "bonbon", "lakritz", "cookie", "waffel", "*kuchen", "*keks", "torte", "muffin", "eis$", "eiscreme", "speiseeis", "*pudding", "dessert", "riegel", "snickers", "twix", "marshmallow", "lebkuchen", "spekulatius", "stollen", "coconut", "kokos mini", "fruchtschnitte"]),
        ("food.groceries.chocolate", ["*schokolade", "schoko", "praline", "milka", "ritter sport", "lindt", "toblerone", "kinder"]),
        ("food.groceries.staples", ["*nudel", "spaghetti", "penne", "fusilli", "tagliatelle", "pasta", "reis$", "basmati", "couscous", "bulgur", "quinoa", "linsen", "bohnen", "kidneybohnen", "kichererbse", "mais$", "konserve", "passierte tomaten", "gehackte tomaten", "stückige tomaten", "tomatenmark", "*kokosmilch", "kokosnussmilch", "mehl", "zucker", "hefe$", "speisestärke", "gnocchi", "schupfnudel", "ramen", "mi goreng", "instant", "*suppe$", "eintopf", "knödel", "oliven", "sauerkraut", "hummus"]),

        ("food.groceries.fish", ["lachs", "thunfisch", "hering", "forelle", "garnelen", "shrimps", "fisch", "makrele", "sardine", "kabeljau", "seelachs", "surimi", "krabben", "muscheln", "tintenfisch", "matjes", "pangasius"]),
        ("food.groceries.dairy", ["milch", "*milch", "vollmilch", "*joghurt", "joghurt", "jogurt", "quark", "*quark", "skyr", "butter", "margarine", "sahne", "*sahne", "schmand", "creme zum kochen", "kochcreme", "cremefine", "crème", "creme fraiche", "cuisine", "*käse", "käse", "mozzarella", "gouda", "edamer", "emmentaler", "parmesan", "feta", "halloumi", "camembert", "brie", "gorgonzola", "mascarpone", "ricotta", "eier", "ei$"]),
        ("food.groceries.meat", ["hähnchen", "hühnchen", "huhn", "pute", "putenbrust", "rind", "schwein", "hack", "gehacktes", "*wurst", "wurst", "salami", "*schinken", "schinken", "bacon", "speck", "fleisch", "steak", "schnitzel", "wiener", "würstchen", "frikadelle", "gyros", "lamm", "ente$", "gans$", "aufschnitt", "mett$", "cevapcici", "kasseler"]),
        ("food.groceries.bread", ["*brot", "*brötchen", "semmel", "*baguette", "toast", "laugen", "croissant", "brezel", "ciabatta", "fladen", "wrap", "tortilla", "pita", "hefezopf", "knäcke", "zwieback", "brioche", "focaccia", "panini"]),
        ("food.groceries.produce", ["banane", "apfel", "äpfel", "birne", "orange", "zitrone", "limette", "mandarine", "clementine", "trauben", "erdbeere", "himbeere", "heidelbeere", "blaubeere", "kirsche", "pflaume", "pfirsich", "nektarine", "melone", "ananas", "mango", "kiwi", "avocado", "tomate", "rispentomaten", "gurke", "*salat", "rucola", "spinat", "möhre", "karotte", "kartoffel", "zwiebel", "lauch", "knoblauch", "ingwer", "paprika", "peperoni", "chili", "brokkoli", "blumenkohl", "*kohl", "zucchini", "aubergine", "kürbis", "pilz", "champignon", "suppengemüse", "kräuter", "petersilie", "basilikum", "schnittlauch", "dill", "koriander", "minze", "sellerie", "radieschen", "rettich", "fenchel", "spargel", "*keimling", "sprossen", "obst", "gemüse", "*pak choi", "schalotte", "süßkartoffel", "rote bete", "datteln", "feigen", "rosinen"])
    ];

    private static readonly (string Key, Func<string, bool>[] Matches)[] Compiled = Groups
        .Select(group => (group.Key, group.Words.Select(Matcher).ToArray()))
        .ToArray();

    /// <summary>Der Schluessel der passenden Kategorie; null, wenn der Name zu keiner Gruppe gehoert.</summary>
    public static string? KeyFor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // "H-Milch 3,5 %" -> " h milch 3 5 ": Wortgrenzen sind alles, was kein Buchstabe oder keine Ziffer ist.
        var text = $" {string.Join(' ', Tokens().Matches(name.ToLowerInvariant()).Select(match => match.Value))} ";
        // Leergut und Pfand sind eine Rueckgabe, keine Ware - auch wenn "Getränke" dasteht.
        if (text.Contains(" leergut") || text.Contains(" pfand")) return null;
        foreach (var (key, matches) in Compiled)
            if (matches.Any(match => match(text))) return key;
        return null;
    }

    /// <summary>Alle Schluessel, die diese Zuordnung vergeben kann; ein Test haelt sie gegen die Standardkategorien.</summary>
    public static IEnumerable<string> Keys => Groups.Select(group => group.Key);

    private static Func<string, bool> Matcher(string word)
    {
        if (word.StartsWith('*')) { var inside = word[1..].TrimEnd('$'); return word.EndsWith('$') ? text => text.Contains($"{inside} ") : text => text.Contains(inside); }
        if (word.EndsWith('$')) { var whole = $" {word[..^1]} "; return text => text.Contains(whole); }
        var start = $" {word}";
        return text => text.Contains(start);
    }

    [GeneratedRegex(@"[\p{L}\d]+")]
    private static partial Regex Tokens();
}
