using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// #115, Schritt 1: "eine ganze Kategorie, mehrere, einzelne Unterkategorien oder Kombinationen
/// daraus".
///
/// Das Backend konnte das seit jeher - <c>BudgetCategories</c> mit <c>IncludeDescendants</c>, dazu
/// <c>/api/budget-scopes/{id}</c> und <c>/api/budget-groups</c> - und hatte null Aufrufer im
/// Frontend. Dasselbe Muster wie die sechs Funktionen in #135.
///
/// Zwei Dinge sind hier gefaehrlicher als sie aussehen und werden deshalb einzeln gepinnt:
/// <c>PUT</c> ersetzt den GANZEN Geltungsbereich (was fehlt, ist geloescht), und der gelesene
/// Bereich ist bei <c>partialAccess</c> NICHT der gespeicherte.
/// </summary>
public sealed class BudgetScopeUiTests
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

    private static string PageJs() => Asset("pages", "budgets", "page.js");

    private static string BodyOf(string js, string pattern, string name)
    {
        var match = Regex.Match(js, pattern, RegexOptions.Singleline);
        Assert.True(match.Success, $"{name} wurde nicht gefunden.");
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

    private static string DialogBody() =>
        BodyOf(PageJs(), @"async function openBudgetDialog\([^)]*\)\s*\{", "openBudgetDialog(...)");

    [Fact]
    public void The_budget_dialog_reads_and_writes_the_scope()
    {
        var body = DialogBody();

        Assert.Contains("api/budget-scopes/${existing.id}", body);
        Assert.Contains("api/budget-scopes/${budgetId}", body);
        Assert.Contains("includeDescendants", body);
    }

    /// <summary>
    /// Ein neues Budget hat seine Id erst, nachdem es angelegt wurde - der Geltungsbereich kann also
    /// nicht parallel geschrieben werden, sondern erst danach, mit der Id aus der Antwort.
    /// </summary>
    [Fact]
    public void The_scope_is_written_after_the_budget_so_a_new_one_has_an_id()
    {
        var body = BodyOf(PageJs(), @"async function save\([^)]*\)\s*\{", "save(...)");

        Assert.Contains("const saved = await ctx.api(", body);
        Assert.Contains("existing?.id || saved?.id", body);
        var budgetIndex = body.IndexOf("const saved = await ctx.api(", StringComparison.Ordinal);
        var scopeIndex = body.IndexOf("api/budget-scopes/${budgetId}", StringComparison.Ordinal);
        Assert.True(scopeIndex > budgetIndex, "Der Geltungsbereich muss nach dem Budget geschrieben werden.");
    }

    /// <summary>
    /// <c>PutScope</c> liest jede Dimension aus der Anfrage und ersetzt sie (<c>request.AccountIds ?? []</c>).
    /// Ein PUT, das nur die Kategorien schickt, loescht damit still Konten, Tags, Haendler, Schwellen
    /// und Gruppe - Einstellungen, die dieser Dialog gar nicht anzeigt und deshalb erst recht nicht
    /// wegnehmen darf. Alle Dimensionen muessen mitgeschickt werden.
    /// </summary>
    [Fact]
    public void Saving_the_scope_preserves_the_dimensions_this_dialog_does_not_edit()
    {
        var body = DialogBody();

        foreach (var field in new[] { "accountIds:", "tagIds:", "merchants:", "incomeScheduleId:",
                                      "alertNearPercent:", "alertCriticalPercent:", "groupId:" })
            Assert.Contains(field, body);
    }

    /// <summary>
    /// <c>RedactAsync</c> entfernt beim Lesen Konten, die dieser Nutzer nicht sehen darf, und setzt
    /// dafuer <c>partialAccess</c>. Das Gelesene ist dann nicht das Gespeicherte - ein Zurueckschreiben
    /// wuerde die unsichtbaren Konten endgueltig aus dem Budget entfernen. Der Bereich bleibt in dem
    /// Fall sichtbar, aber unveraenderbar.
    /// </summary>
    [Fact]
    public void A_partially_visible_scope_is_never_written_back()
    {
        var body = DialogBody();

        Assert.Contains("const scopeEditable = !scope?.partialAccess;", body);
        Assert.Contains("scopeEditable && scopeChanged()", body);
        // Und der Knopf laesst sich gar nicht erst oeffnen.
        Assert.Contains("if (scopeEditable) scopeRow?.addEventListener('click'", body);
    }

    /// <summary>
    /// Nur bei echter Aenderung schreiben - sonst setzt jedes Umbenennen eines Budgets seine
    /// Kategorien neu. Der Vergleich muss reihenfolgeunabhaengig sein: die Auswahlliste liefert in
    /// Zeilenreihenfolge, der Server in seiner eigenen.
    /// </summary>
    [Fact]
    public void The_scope_is_only_written_when_it_actually_changed()
    {
        var body = DialogBody();

        Assert.Contains("const scopeChanged = () =>", body);
        Assert.Contains("new Map(initialScope.map(", body);
        // Kein Vergleich ueber join()/JSON.stringify, der auf Reihenfolge hereinfaellt.
        Assert.DoesNotContain("JSON.stringify(scopeCategories)", body);
    }

    /// <summary>
    /// Der Server laesst einen gesetzten Geltungsbereich die einzelne <c>Budgets.CategoryId</c>
    /// ueberschreiben (<c>BudgetReconciliationService.LoadScopeAsync</c> nimmt die Altfassung nur,
    /// wenn kein Bereich existiert). Die Oberflaeche muss dasselbe sagen: ein Auswahlfeld, das noch
    /// bedienbar aussieht, aber nichts mehr bewirkt, ist die schlechtere Haelfte dieser Regel.
    /// </summary>
    [Fact]
    public void The_single_category_field_shows_when_the_scope_overrides_it()
    {
        var body = DialogBody();

        Assert.Contains("select.disabled = overridden", body);
        Assert.Contains("budgets.scopeOverrides", body);
        Assert.Contains("is-overridden", body);
        Assert.Contains(".fw-field.is-overridden", Asset("pages", "budgets", "page.css"));
    }

    /// <summary>Wiederverwendung statt Nachbau: die Mehrfachauswahl ist die gemeinsame Liste (#160),
    /// die Knoepfe tragen die gemeinsamen Rollen (#158).</summary>
    [Fact]
    public void The_picker_reuses_the_shared_selection_list()
    {
        var body = DialogBody();

        Assert.Contains("createSelectionList()", body);
        Assert.Contains("selectionListHtml(items", body);
        Assert.Contains("buttonClass(ButtonRole.Primary)", body);
    }

    [Fact]
    public void Both_locales_carry_the_scope_texts()
    {
        foreach (var locale in new[] { "de", "en" })
        {
            var json = Asset("locales", $"{locale}.json");
            foreach (var key in new[] { "scopeTitle", "scopeNone", "scopeHint",
                                        "scopeIncludeChildren", "scopeOverrides", "scopePartial" })
                Assert.Contains($"\"{key}\"", json);
        }
    }
}
