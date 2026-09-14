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
