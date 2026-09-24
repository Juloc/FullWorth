namespace FullWorth.Web.Tests;

/// <summary>
/// Die Oberflaeche der Budget-Gruppen (#177).
///
/// Der Backend-Teil steht in <c>BudgetGroupReachabilityTests</c>. Hier geht es um die andere Haelfte
/// desselben Fehlers: die vier Routen hatten keinen Aufrufer, und das <c>groupId</c> im
/// Geltungsbereich war ein Feld, das der Dialog treu hin- und herschickte, ohne dass es je jemand
/// setzen konnte.
///
/// Drei Dinge muessen deshalb beieinander bleiben, und alle drei koennen einzeln verschwinden, ohne
/// dass irgendetwas kaputt AUSSIEHT:
///
/// <list type="number">
///   <item>Der Weg zur Verwaltung. Ohne ihn entsteht nie eine Gruppe, und alles andere ist tot.</item>
///   <item>Das Feld im Budget-Dialog. Ohne es bleibt die Zuordnung unvergebbar - genau der Zustand
///         vor #177.</item>
///   <item>Dass die Zuordnung auch GESCHRIEBEN wird, wenn sich sonst nichts geaendert hat. Der
///         Geltungsbereich wurde bis dahin nur bei geaenderten Kategorien zurueckgeschrieben; eine
///         neu gewaehlte Gruppe waere still liegengeblieben.</item>
/// </list>
/// </summary>
public sealed class BudgetGroupUiTests
{
    private static string Seite() => WebSources.Asset("pages", "budgets", "page.js");
    private static string Gruppen() => WebSources.Asset("pages", "budgets", "groups.js");

    [Fact]
    public void The_manager_is_reachable_from_the_page_header()
    {
        Assert.Contains("data-action=\"budget-groups\"", WebSources.Page("Budgets"), StringComparison.Ordinal);
        Assert.Contains("openBudgetGroupManager", WebSources.Asset("pages", "budgets", "entry.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void All_four_routes_have_a_caller()
    {
        var quelle = Gruppen();

        Assert.Contains("ctx.api('api/budget-groups')", quelle, StringComparison.Ordinal);
        Assert.Contains("'api/budget-groups'", quelle, StringComparison.Ordinal);
        Assert.Contains("api/budget-groups/${existing.id}", quelle, StringComparison.Ordinal);
        Assert.Contains("api/budget-groups/${group.id}`, { method: 'DELETE' }", quelle, StringComparison.Ordinal);
    }

    [Fact]
    public void The_budget_dialog_can_choose_a_group()
    {
        var quelle = Seite();

        Assert.Contains("name: 'group'", quelle, StringComparison.Ordinal);
        Assert.Contains("budgetGroupOptions(ctx, groups, scope?.groupId)", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die leiseste der drei Stellen: der Geltungsbereich wird nur zurueckgeschrieben, wenn er sich
    /// geaendert hat - sonst wuerde jedes Umbenennen eines Budgets seine Kategorien neu setzen. Eine
    /// neu gewaehlte Gruppe ist aber genau so eine Aenderung, und ohne diese Bedingung bliebe sie im
    /// Dialog stehen und waere nach dem Speichern weg.
    /// </summary>
    [Fact]
    public void A_changed_group_alone_is_enough_to_write_the_scope()
    {
        var quelle = Seite();

        Assert.Contains("scopeChanged() || groupChanged", quelle, StringComparison.Ordinal);
        Assert.Contains("const groupChanged = String(scope?.groupId || '') !== String(groupId || '')", quelle, StringComparison.Ordinal);
        // Und geschrieben wird der GEWAEHLTE Wert, nicht der gelesene.
        Assert.DoesNotContain("groupId: scope?.groupId ?? null", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ueberschriften nur, wenn es Gruppen GIBT. Eine einzige Ueberschrift ueber der ganzen Liste
    /// ordnet nichts und kostet jede Instanz ohne Gruppen eine Zeile.
    /// </summary>
    [Fact]
    public void The_list_only_groups_when_there_is_something_to_group()
    {
        var quelle = Seite();

        Assert.Contains("grouped.length", quelle, StringComparison.Ordinal);
        Assert.Contains("budget-group-heading", quelle, StringComparison.Ordinal);
        Assert.Contains("accounts.ungrouped", quelle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Das Blatt gehoert in den Zwischenspeicher der PWA. Ein Modul, das die Seite importiert und
    /// das der Service Worker nicht kennt, macht die Seite offline leer - dieselbe Falle wie bei den
    /// siebzehn entry.js in #154.
    /// </summary>
    [Fact]
    public void The_manager_is_precached()
    {
        Assert.Contains("/pages/budgets/groups.js", WebSources.Asset("sw.js"), StringComparison.Ordinal);
    }
}
