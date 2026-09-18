namespace FullWorth.Web.Tests;

/// <summary>
/// The searchable picker, and the two decisions that keep it from becoming a liability.
///
/// It was the category picker's machinery, available for exactly one kind of list: categories had
/// search over full paths and icons, while accounts — six dialogs — had a bare select. Generalising it
/// is only safe because of how it works: it LAYERS over a native select and sets its value, so the
/// app's other selects keep working untouched, FormData included.
/// </summary>
public sealed class ComboboxUiBaselineTests
{
    [Fact]
    public void The_combobox_sets_a_native_select_rather_than_replacing_it()
    {
        var combobox = ReadSource(Path.Combine("components", "combobox.js"));

        // The whole contract in one line: existing forms read the select, so the select must stay the
        // value. Replacing it with a custom control would silently empty every FormData in the app.
        Assert.Contains("selectEl.value = id", combobox, StringComparison.Ordinal);
        Assert.Contains("new Event('change', { bubbles: true })", combobox, StringComparison.Ordinal);
        Assert.Contains("export function itemsFromSelect", combobox, StringComparison.Ordinal);
        // Attaching twice must not grow a second button.
        Assert.Contains("selectEl.dataset.combobox === 'on'", combobox, StringComparison.Ordinal);
    }

    /// <summary>
    /// The category picker keeps its public shape — four call sites depend on it — but not its own
    /// copy of the dialog, the search, the icon rows or the keyboard. Only what is actually about
    /// categories stays: the path label and the inline create form.
    /// </summary>
    [Fact]
    public void The_category_picker_is_a_thin_layer_on_the_shared_combobox()
    {
        var picker = ReadSource(Path.Combine("pages", "transactions", "category-picker.js"));

        Assert.Contains("import { attachCombobox, openCombobox } from '../../components/combobox.js';", picker, StringComparison.Ordinal);
        Assert.Contains("export function attachCategoryPicker", picker, StringComparison.Ordinal);
        Assert.Contains("export async function openCategoryPicker", picker, StringComparison.Ordinal);

        // No second implementation of the list: those belong to the combobox now.
        Assert.DoesNotContain("data-search", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate-row", picker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opt-in, not automatic. Most of this app's selects are short — a three-option "Typ" is better
    /// native, because the phone's own wheel and type-ahead beat a custom dialog.
    /// </summary>
    [Fact]
    public void A_dialog_select_only_becomes_searchable_when_the_field_asks()
    {
        var formDialog = ReadSource(Path.Combine("components", "form-dialog.js"));

        Assert.Contains("f.searchable && f.kind === FieldKind.Select", formDialog, StringComparison.Ordinal);
        Assert.Contains("comboboxCtx = null", formDialog, StringComparison.Ordinal);
        Assert.Contains("if (comboboxCtx)", formDialog, StringComparison.Ordinal);
    }

    /// <summary>The long lists it was generalised for actually use it.</summary>
    [Fact]
    public void The_account_pickers_are_searchable()
    {
        // Verträge sind eine Seite geworden, Kredite gehören noch zu Vermögen und liegen weiter in
        // features/ — beide benutzen dieselbe durchsuchbare Kontenauswahl.
        foreach (var file in new[] { Path.Combine("pages", "contracts", "page.js"), Path.Combine("pages", "networth", "loans.js") })
        {
            var source = ReadSource(file);
            Assert.Contains("comboboxCtx: ctx", source, StringComparison.Ordinal);
            Assert.Contains("searchable: true", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// #121: the remaining long category selects (dozens of options, hierarchical "A › B › C" labels)
    /// that still opened as a select-plus-search-button-plus-drawer are now anchored fields - the
    /// popover opens AT the field, shows the current value and its icon closed, and the six sites all
    /// feed it through the one shared transformer instead of each keeping its own path-building copy.
    /// </summary>
    [Fact]
    public void The_remaining_long_category_selects_are_anchored()
    {
        // A dialog built with components/form-dialog.js only needs `anchored: true` plus
        // `comboboxItems` on the field spec (and still `comboboxCtx` to opt the dialog in at all).
        foreach (var file in new[] { Path.Combine("pages", "rules", "page.js"), Path.Combine("pages", "budgets", "page.js") })
        {
            var source = ReadSource(file);
            Assert.Contains("comboboxCtx: ctx", source, StringComparison.Ordinal);
            Assert.Contains("anchored: true", source, StringComparison.Ordinal);
            Assert.Contains("comboboxItems:", source, StringComparison.Ordinal);
        }

        // The booking filter's category field is one of six fields on the visible row, not behind
        // "Mehr Filter" - the worst offender in the original census (docs/UI_AUDIT.md) is also anchored now.
        var transactions = ReadSource(Path.Combine("pages", "transactions", "page.js"));
        Assert.Contains("comboboxCtx: ctx", transactions, StringComparison.Ordinal);
        Assert.Contains("anchored: true", transactions, StringComparison.Ordinal);
        Assert.Contains("comboboxItems:", transactions, StringComparison.Ordinal);

        // Hand-built dialogs (no components/form-dialog.js involved) wire attachCombobox directly on
        // their own category <select> with `anchored: true` in the options object.
        foreach (var file in new[] { Path.Combine("pages", "categories", "page.js"), Path.Combine("pages", "purchases", "page.js") })
        {
            var source = ReadSource(file);
            Assert.Contains("attachCombobox", source, StringComparison.Ordinal);
            Assert.Contains("anchored: true", source, StringComparison.Ordinal);
        }

        // These two modules deliberately have no app `ctx` (own esc/api/makeDialog, see their own file
        // header comments) - each carries its own minimal adapter rather than reaching for the app ctx.
        foreach (var file in new[] { Path.Combine("pages", "purchases", "articles-advanced-actions.js"), Path.Combine("pages", "purchases", "articles-workspace.js") })
        {
            var source = ReadSource(file);
            Assert.Contains("function comboboxCtx(", source, StringComparison.Ordinal);
            Assert.Contains("attachCombobox(comboboxCtx(", source, StringComparison.Ordinal);
            Assert.Contains("anchored: true", source, StringComparison.Ordinal);
        }

        // Every one of the six sites feeds the shared transformer - not a seventh copy of the
        // path-building it replaces.
        foreach (var file in new[]
        {
            Path.Combine("pages", "rules", "page.js"), Path.Combine("pages", "budgets", "page.js"),
            Path.Combine("pages", "transactions", "page.js"), Path.Combine("pages", "categories", "page.js"),
            Path.Combine("pages", "purchases", "page.js"), Path.Combine("pages", "purchases", "articles-advanced-actions.js"),
            Path.Combine("pages", "purchases", "articles-workspace.js")
        })
        {
            Assert.Contains("categoryComboboxItems(", ReadSource(file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Opt-in stays opt-in even as `anchored` spreads: the account pickers (six dialogs) are
    /// deliberately still a select next to a search button, not a field popover, and nothing here
    /// should quietly promote them just because they sit next to `searchable: true`.
    /// </summary>
    [Fact]
    public void The_account_pickers_stay_unanchored()
    {
        foreach (var file in new[] { Path.Combine("pages", "contracts", "page.js"), Path.Combine("pages", "networth", "loans.js") })
        {
            var source = ReadSource(file);
            Assert.Contains("searchable: true", source, StringComparison.Ordinal);
            Assert.DoesNotContain("anchored: true", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The path-building (full path as `hint`, indentation, icon inheritance) that used to be the
    /// category picker's own now lives in one shared, page-blind transformer - so every other category
    /// select can reuse it instead of copying it a sixth or seventh time.
    /// </summary>
    [Fact]
    public void The_category_path_logic_lives_in_one_shared_component()
    {
        var component = ReadSource(Path.Combine("components", "category-combobox.js"));
        Assert.Contains("export function categoryComboboxItems", component, StringComparison.Ordinal);

        var picker = ReadSource(Path.Combine("pages", "transactions", "category-picker.js"));
        Assert.Contains("import { categoryComboboxItems } from '../../components/category-combobox.js';", picker, StringComparison.Ordinal);

        // No leftover second copy of the path-building it replaced.
        Assert.DoesNotContain("function chainOf", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("function inheritedIcon", picker, StringComparison.Ordinal);
    }

    /// <summary>
    /// #121's other fix: the row already chosen had no visual sign of it in the open list at all.
    /// </summary>
    [Fact]
    public void The_open_list_marks_the_current_row()
    {
        var combobox = ReadSource(Path.Combine("components", "combobox.js"));
        Assert.Contains("aria-selected", combobox, StringComparison.Ordinal);
        Assert.Contains(".selected", combobox, StringComparison.Ordinal);

        Assert.Contains(".candidate-row.selected", ReadSource(Path.Combine("styles", "components.css")), StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(
            Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot", relativePath));
    }
}
