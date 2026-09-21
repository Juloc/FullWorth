using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #124: die Such-/Filteransicht im Sammlungsdetail.
///
/// Die Vorschlagsliste daneben beantwortet "was koennte dazugehoeren". Diese hier beantwortet die
/// andere Frage: jemand WEISS, was er sucht - die Tankstelle auf der Rueckfahrt, alle Baumarktkaeufe
/// im Maerz - und der Vorschlag findet sie nicht, weil weder Haendler noch Kategorie passen.
///
/// Kein neuer Endpunkt: gesucht wird ueber <c>/api/transactions</c> mit seinen vorhandenen Filtern,
/// und welche Buchungen schon drin sind, steht im Sammlungsdetail (<c>transactionIds</c>).
/// </summary>
public sealed class CollectionTransactionSearchUiTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Asset(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root(), "src", "FullWorth.Web", "wwwroot", .. parts]));

    private static string PageJs() => Asset("pages", "collections", "page.js");

    private static string SearchBody()
    {
        var js = PageJs();
        var match = Regex.Match(js, @"async function openTransactionSearch\([^)]*\)\s*\{", RegexOptions.Singleline);
        Assert.True(match.Success, "openTransactionSearch(...) wurde nicht gefunden.");
        var start = match.Index + match.Length;
        var depth = 1;
        var end = start;
        while (depth > 0 && end < js.Length)
        {
            if (js[end] == '{') depth++;
            else if (js[end] == '}') depth--;
            end++;
        }
        return js[start..end];
    }

    [Fact]
    public void The_collection_detail_offers_a_search_next_to_the_suggestions()
    {
        Assert.Contains("id=\"col-search-transactions\"", Asset("pages", "collections", "page.html"));
        var js = PageJs();
        Assert.Contains("openTransactionSearch(row, detail.transactionIds || [])", js);
        // Die Vorschlagsliste bleibt daneben bestehen - zwei Fragen, zwei Wege.
        Assert.Contains("openCandidates(row)", js);
    }

    /// <summary>
    /// Gesucht wird ueber die vorhandene Buchungssuche. Eine zweite Suchimplementierung neben der
    /// Buchungsseite waere eine zweite Stelle, an der ein Filter etwas anderes bedeutet.
    /// </summary>
    [Fact]
    public void The_search_reuses_the_transaction_endpoint_and_its_filters()
    {
        var body = SearchBody();

        Assert.Contains("api/transactions?${query}", body);
        foreach (var filter in new[] { "query", "from", "to", "accountId", "categoryId", "minAmount", "maxAmount" })
            Assert.Contains($"'{filter}'", body);
    }

    /// <summary>
    /// Schon zugeordnete Treffer werden gekennzeichnet, nicht stillschweigend weggelassen: weggelassen
    /// liesse den Benutzer raten, ob die Suche sie nicht gefunden hat oder sie laengst drin sind. Wer
    /// sie nicht sehen will, schaltet sie weg - das ist eine Entscheidung, kein Zustand.
    /// </summary>
    [Fact]
    public void Already_assigned_hits_are_marked_and_can_be_hidden_on_request()
    {
        var body = SearchBody();

        Assert.Contains("alreadyAssigned", body);
        Assert.Contains("col-search-assigned", body);
        Assert.Contains("unassignedOnly", body);
        // Die Zugehoerigkeit kommt aus dem Detail, nicht aus einer Abfrage je Zeile.
        Assert.Contains("new Set(assignedIds)", body);
    }

    /// <summary>Wiederverwendung: die gemeinsame Auswahlliste (#160) und die Button-Rollen (#158).</summary>
    [Fact]
    public void The_result_list_reuses_the_shared_primitives()
    {
        var body = SearchBody();

        Assert.Contains("createSelectionList()", body);
        Assert.Contains("selectionListHtml(items", body);
        Assert.Contains("buttonClass(ButtonRole.Primary)", body);
        Assert.DoesNotContain("type=\"checkbox\" data-select", body);
    }

    /// <summary>
    /// Zugeordnet wird ueber denselben Endpunkt wie ueberall sonst, und zwar HINZUFUEGEND - eine
    /// Suche darf nie ersetzen, was sie nicht anzeigt.
    /// </summary>
    [Fact]
    public void Assigning_from_the_search_adds_and_never_replaces()
    {
        var body = SearchBody();

        Assert.Contains("api/collections/${row.id}/transactions", body);
        Assert.DoesNotContain("'PUT'", body);
        Assert.DoesNotContain("/remove", body);
    }

    [Fact]
    public void Both_locales_carry_the_search_texts()
    {
        foreach (var locale in new[] { "de", "en" })
        {
            var json = Asset("locales", $"{locale}.json");
            foreach (var key in new[] { "searchTransactions", "onlyUnassigned", "alreadyAssigned", "minAmount", "maxAmount" })
                Assert.Contains($"\"{key}\"", json);
        }
    }
}
