namespace FullWorth.Web.Tests;

/// <summary>
/// Der Weg zum Zusammenfuehren zweier Kategorien (#177).
///
/// Beide Routen standen fertig im Baum und hatten keinen Aufrufer. Wer zwei Kategorien fuer dieselbe
/// Sache hatte - nach jedem Import der Normalfall - konnte sie nur an jeder Buchung einzeln
/// umhaengen.
///
/// Was hier gehalten wird, ist die Reihenfolge: erst die Vorschau, dann die Tat. Zusammenfuehren
/// aendert rueckwirkend jede Auswertung, jede Regel, jedes Budget und jeden Vertrag, der an der
/// Quelle hing - und es laesst sich nicht rueckgaengig machen. Ein Dialog, der die Zahl erst
/// HINTERHER nennt, hat die Frage nicht gestellt.
/// </summary>
public sealed class CategoryMergeUiTests
{
    private static string Modul() => WebSources.Asset("pages", "categories", "merge.js");

    [Fact]
    public void It_is_reachable_from_the_edit_dialog_and_precached()
    {
        var seite = WebSources.Asset("pages", "categories", "page.js");

        Assert.Contains("openCategoryMerge", seite, StringComparison.Ordinal);
        Assert.Contains("data-merge", seite, StringComparison.Ordinal);
        Assert.Contains("/pages/categories/merge.js", WebSources.Asset("sw.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void Both_routes_have_a_caller()
    {
        var quelle = Modul();

        Assert.Contains("api/category-merge/${source.id}/preview?targetCategoryId=", quelle, StringComparison.Ordinal);
        Assert.Contains("api/category-merge/${source.id}`,", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Knopf folgt dem Urteil des Servers, nicht einer eigenen Rechnung. Die beiden Gruende, warum
    /// es nicht geht - aktive Unterkategorien und ein Ziel unterhalb der Quelle - hier ein zweites Mal
    /// auszurechnen waere die zweite Wahrheit, die irgendwann von der ersten abweicht.
    /// </summary>
    [Fact]
    public void The_apply_button_follows_the_servers_verdict()
    {
        var quelle = Modul();

        Assert.Contains("apply.disabled = !result.canApply", quelle, StringComparison.Ordinal);
        Assert.Contains("deleteRow.hidden = !result.canDeleteSource", quelle, StringComparison.Ordinal);
        // Und die Gruende stehen dabei, statt dass der Knopf nur stumm aus bleibt.
        Assert.Contains("result.activeChildren > 0", quelle, StringComparison.Ordinal);
        Assert.Contains("result.targetIsDescendant", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Vorschau vor der Tat, und dazwischen eine Rueckfrage. Beides zusammen, weil eine Zahl allein
    /// noch keine Entscheidung ist und eine Rueckfrage ohne Zahl nur eine Huerde.
    /// </summary>
    [Fact]
    public void The_preview_comes_first_and_the_merge_asks_again()
    {
        var quelle = Modul();

        Assert.Contains("select.addEventListener('change', () => void load())", quelle, StringComparison.Ordinal);
        Assert.Contains("await ctx.confirm(", quelle, StringComparison.Ordinal);
        Assert.Contains("destructive: true", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die Quelle selbst, alles darunter und jede archivierte Kategorie scheiden als Ziel aus. Das
    /// erste waere sinnlos, das zweite schloesse einen Kreis, und ins Archiv hinein zusammenzufuehren
    /// hiesse, Buchungen in eine Kategorie zu schieben, die niemand mehr waehlen kann.
    /// </summary>
    [Fact]
    public void The_target_list_leaves_out_what_cannot_be_a_target()
    {
        var quelle = Modul();

        Assert.Contains("function bannedTargets(source, all)", quelle, StringComparison.Ordinal);
        Assert.Contains("banned.add(category.id)", quelle, StringComparison.Ordinal);
        Assert.Contains("if (category.isArchived)", quelle, StringComparison.Ordinal);
        // Und es ist dieselbe Auswahlliste wie ueberall sonst, kein sechstes Kategoriefeld.
        Assert.Contains("categoryComboboxItems(all, { exclude: banned })", quelle, StringComparison.Ordinal);
    }

    [Fact]
    public void The_labels_exist_in_both_languages()
    {
        foreach (var sprache in new[] { "de", "en" })
        {
            var locale = WebSources.Asset("locales", $"{sprache}.json");
            foreach (var key in new[] { "\"merge\"", "\"mergeTarget\"", "\"mergeDeleteSource\"", "\"mergeTransactions\"" })
                Assert.Contains(key, locale, StringComparison.Ordinal);
        }
    }
}
