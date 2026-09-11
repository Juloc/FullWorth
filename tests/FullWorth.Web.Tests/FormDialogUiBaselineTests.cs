namespace FullWorth.Web.Tests;

/// <summary>
/// The form primitive the dialogs never had (<c>ui/form-dialog.js</c>, step 1 of the dialog plan in
/// docs/UI_AUDIT.md).
///
/// The audit measured the cause of the dialogs reading as over-complex: <c>createDialog</c> gives only a
/// shell, so each of 76 call sites independently re-decided field grouping, whether anything is optional,
/// where a validation error appears, and what the actions row looks like. Nothing shared existed to be
/// consistent with. These pin the decisions the new module now owns, because each of them is a rule that
/// would otherwise be re-litigated at the next call site — and the census already grew from 73 to 76
/// during the audit itself.
/// </summary>
public sealed class FormDialogUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient client;

    public FormDialogUiBaselineTests(FullWorthWebFactory factory) => client = factory.CreateClient();

    /// <summary>
    /// Grouping and progressive disclosure are the two things a flat list of 14 equally weighted controls
    /// lacked. Both have to be the caller's statement about meaning: a layout heuristic would pair
    /// "Betrag" with "Notiz" because they happen to be adjacent.
    /// </summary>
    [Fact]
    public async Task The_form_dialog_supports_grouped_rows_and_progressive_disclosure()
    {
        var js = await GetAsync("/ui/form-dialog.js");
        var css = await GetAsync("/dialogs.css");

        Assert.Contains("export function createFormDialog", js);
        Assert.Contains("export function openFormDialog", js);
        Assert.Contains("function groupFields", js);
        // `advanced` fields live in a <details>, not in a second dialog: a nested dialog would lose the
        // values already typed, which is the very failure the disclosure exists to avoid.
        Assert.Contains("<details class=\"fw-dialog-advanced\"", js);
        Assert.DoesNotContain("createDialog(advanced", js);
        Assert.Contains(".fw-dialog-advanced", css);
        Assert.Contains(".fw-field-row", css);
    }

    /// <summary>
    /// A grouped row needs its own grid. <c>.rule-grid</c> looks like the shared two-column primitive but
    /// its only base rule is scoped under <c>.rule-dialog</c>, so a bare <c>.rule-grid</c> is
    /// <c>display:block</c> — measured: grouped fields silently stacked.
    /// </summary>
    [Fact]
    public async Task A_grouped_row_does_not_rely_on_the_rule_dialog_scoped_grid()
    {
        var js = await GetAsync("/ui/form-dialog.js");
        var css = await GetAsync("/dialogs.css");

        Assert.DoesNotContain("class=\"rule-grid fw-field-row\"", js);
        Assert.Contains(".fw-form-dialog .fw-field-row{display:grid", css);
        // Shrinkable columns, or one long <option> sizes the whole card again (the overflow this pass fixed).
        Assert.Contains("minmax(0,1fr)", css);
    }

    /// <summary>
    /// An error belongs under the field that is wrong, it must be in the DOM from the start so a failed
    /// validation does not reflow the card and push the message off screen, and it must open the
    /// disclosure when the offending field is hidden — an error the user cannot see is indistinguishable
    /// from a form that refuses to submit for no reason.
    /// </summary>
    [Fact]
    public async Task A_validation_error_appears_at_its_own_field_and_reveals_a_hidden_one()
    {
        var js = await GetAsync("/ui/form-dialog.js");
        var css = await GetAsync("/dialogs.css");

        Assert.Contains("data-error-for=", js);
        Assert.Contains("closest('details')?.setAttribute('open'", js);
        Assert.Contains(".fw-field-error", css);
        // Empty means absent, so the slot takes no space until there is something to say.
        Assert.Contains(".fw-form-dialog .fw-field-error:empty{display:none}", css);
    }

    /// <summary>
    /// The destructive action is pushed away from the primary one rather than merely coloured: colour
    /// alone still leaves "Löschen" one thumb-slip from "Speichern" on a 375 px screen.
    /// </summary>
    [Fact]
    public async Task The_destructive_action_is_separated_from_the_primary_one()
    {
        var js = await GetAsync("/ui/form-dialog.js");
        var css = await GetAsync("/dialogs.css");

        Assert.Contains("fw-actions-spacer", js);
        Assert.Contains(".fw-dialog-actions .fw-actions-spacer", css);
        // Roles only — no hand-rolled button styling anywhere in the module.
        Assert.Contains("buttonClass(", js);
        Assert.DoesNotContain("style=", js);
    }

    /// <summary>
    /// An empty number field is <b>absent</b>, not zero. <c>Number('')</c> is 0 and finite, which is how
    /// an unset value becomes a real 0,00 € in a payload — the same trap the wealth page hit with
    /// <c>Number(null)</c>.
    /// </summary>
    [Fact]
    public async Task An_empty_number_field_reads_as_missing_rather_than_zero()
    {
        var js = await GetAsync("/ui/form-dialog.js");

        Assert.Contains("raw === '' ? null : Number(raw)", js);
    }

    /// <summary>
    /// A set filter that is invisible silently changes what the user is looking at, which is worse than a
    /// long form. So the closed disclosure always says how many of its fields carry a value.
    /// </summary>
    [Fact]
    public async Task The_closed_disclosure_says_how_many_hidden_fields_are_set()
    {
        var js = await GetAsync("/ui/form-dialog.js");
        var css = await GetAsync("/dialogs.css");

        Assert.Contains("data-advanced-count", js);
        Assert.Contains("advanced.filter(isSet).length", js);
        Assert.Contains(".fw-advanced-count", css);
    }

    /// <summary>
    /// Focus starts on the first field, not on the close button — a dialog that opens with the close
    /// button focused answers Enter with "cancel".
    /// </summary>
    [Fact]
    public async Task Focus_starts_on_the_first_field()
    {
        var js = await GetAsync("/ui/form-dialog.js");

        Assert.Contains("first?.focus()", js);
    }

    /// <summary>
    /// Step 1 changes no call site on purpose, so it cannot break a screen. This pins that: the module is
    /// served and self-contained, and no feature imports it yet.
    /// </summary>
    [Fact]
    public async Task Step_one_adds_the_layer_without_touching_a_call_site()
    {
        var js = await GetAsync("/ui/form-dialog.js");

        Assert.Contains("import { createDialog } from './dialog.js'", js);
        Assert.Contains("import { ButtonRole, buttonClass } from './buttons.js'", js);
        // It owns markup and behaviour, never fetching or saving: a caller gets values and decides.
        Assert.DoesNotContain("fetch(", js);
        Assert.DoesNotContain("api(", js);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
