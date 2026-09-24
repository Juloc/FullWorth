namespace FullWorth.Web.Tests;

/// <summary>
/// Die letzten vier Leser aus #177, die eine Oberflaeche verdient haben.
///
/// Drei davon lagen direkt neben ihren Geschwistern und waren als einzige nicht eingebunden - das
/// ist die haeufigste Form dieses Fehlers in diesem Haus und die leiseste: nichts sieht kaputt aus,
/// es fehlt nur eine Zeile.
///
/// <list type="bullet">
///   <item><c>purchase-analytics/by-merchant</c> war die fuenfte von fuenf gleichartigen
///         Gruppierungen. Vier standen in der Ansicht, diese nicht.</item>
///   <item><c>product-learning/category-suggestions</c> hatte Vorschlag UND Annahme fertig gebaut,
///         und keinen von beiden rief jemand.</item>
///   <item><c>tax/profiles</c> lag neben <c>tax/profile/settings</c>, das benutzt wird.</item>
///   <item><c>purchases/discount-analytics</c> war ein Doppel: die aermere
///         <c>purchase-analytics/savings</c> war eingebunden, die reichere nicht.</item>
/// </list>
/// </summary>
public sealed class PurchaseAndTaxReachabilityUiTests
{
    private static string Workspace() => WebSources.Asset("pages", "purchases", "articles-workspace.js");
    private static string PriceInsights() => WebSources.Asset("pages", "purchases", "price-insights.js");
    private static string Surface() => File.ReadAllText(Path.Combine(WebSources.RepoRoot,
        "tests", "FullWorth.Backend.Tests", "Architecture", "route-surface.txt"));

    /// <summary>
    /// Alle fuenf Gruppierungen in einem Abruf - und darauf kommt es an: die fuenfte nachtraeglich
    /// in einem zweiten Aufruf zu holen waere derselbe Fehler noch einmal, nur langsamer.
    /// </summary>
    [Fact]
    public void All_five_spend_groupings_are_loaded_together()
    {
        var quelle = Workspace();

        foreach (var route in new[] { "by-category", "by-product", "by-brand", "by-merchant" })
            Assert.Contains($"api/purchase-analytics/{route}'", quelle, StringComparison.Ordinal);
        Assert.Contains("analyticsCard(t('topMerchants')", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die gelernten Kategorien zeigen die ZAHL: "5 von 6 mal Lebensmittel" ist eine andere Aussage
    /// als "Lebensmittel", und ohne sie waere die Annahme ein Vertrauensvorschuss statt einer
    /// Entscheidung.
    /// </summary>
    [Fact]
    public void Learned_categories_can_be_seen_and_accepted()
    {
        var quelle = Workspace();

        Assert.Contains("api/product-learning/category-suggestions'", quelle, StringComparison.Ordinal);
        Assert.Contains("api/product-learning/category-suggestions/accept", quelle, StringComparison.Ordinal);
        Assert.Contains("row.count}/${row.totalOccurrences}", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Schwellen bleiben auf dem Server. "Ab drei gleichen Zuordnungen, und nur bei eindeutigem
    /// Gewinner" hier ein zweites Mal aufzuschreiben hiesse, zwei Antworten auf dieselbe Frage zu
    /// haben - und die zweite wuerde beim naechsten Umbau der ersten nicht mitwandern.
    /// </summary>
    [Fact]
    public void The_learning_thresholds_are_not_repeated_in_the_browser()
    {
        var quelle = Workspace();
        var start = quelle.IndexOf("async function renderLearningSuggestions()", StringComparison.Ordinal);
        var ende = quelle.IndexOf("async function openProductCreate", StringComparison.Ordinal);
        Assert.True(start >= 0 && ende > start, "Der Abschnitt steht nicht mehr, wo dieser Test ihn sucht.");
        var abschnitt = quelle[start..ende];

        Assert.DoesNotContain(">= 3", abschnitt, StringComparison.Ordinal);
        Assert.DoesNotContain("filter(", abschnitt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Rabattansicht liest die reichere Antwort - und die aermere ist weg, damit nicht wieder
    /// jemand die falsche der beiden verdrahtet.
    /// </summary>
    [Fact]
    public void The_savings_card_reads_the_richer_answer_and_the_poorer_route_is_gone()
    {
        var quelle = PriceInsights();

        Assert.Contains("api/purchase-analytics/discount-analytics", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("api/purchase-analytics/savings", quelle, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/purchase-analytics/savings", Surface(), StringComparison.Ordinal);
        // Was der Umzug dazugewonnen hat, steht auch auf dem Schirm.
        Assert.Contains("savings.byMerchant", quelle, StringComparison.Ordinal);
        Assert.Contains("savings.purchasesWithDiscount", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Und die Zahl je Rabattart ist NICHT verlorengegangen: sie kam aus der geloeschten Route und
    /// steht dafuer jetzt in der Aufschluesselung der anderen.
    /// </summary>
    [Fact]
    public void The_count_per_discount_type_survived_the_move()
    {
        Assert.Contains("Number(row.count || 0)}×", PriceInsights(), StringComparison.Ordinal);
        Assert.Contains(
            "PurchaseDiscountAnalyticsBreakdown(string Name, decimal Amount, int Count)",
            File.ReadAllText(Path.Combine(WebSources.RepoRoot,
                "src", "FullWorth.Backend", "Modules", "Purchases", "PurchaseDiscountAnalytics.cs")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Steuerprofile erscheinen nur, wenn es mehr als eines gibt. Eine Liste, in der man sich
    /// selbst einmal sieht, ordnet nichts und nimmt einer Seite Platz weg, auf der es um Betraege
    /// geht.
    /// </summary>
    [Fact]
    public void Tax_profiles_appear_only_when_there_is_more_than_one()
    {
        var quelle = WebSources.Asset("pages", "tax", "page.js");

        Assert.Contains("api/tax/profiles", quelle, StringComparison.Ordinal);
        Assert.Contains("rows.length < 2) return;", quelle, StringComparison.Ordinal);
        // Wer was sieht, entscheidet der Server - hier wird nicht nach Rolle gefiltert.
        Assert.DoesNotContain("isOwner", quelle, StringComparison.Ordinal);
    }
}
