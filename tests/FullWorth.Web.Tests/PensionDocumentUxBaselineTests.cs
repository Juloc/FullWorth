using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// The bAV document flow (step 2 of docs/PENSION.md) is upload → review → commit, and three of its
/// properties are promises to the user rather than implementation details:
///
/// <list type="number">
///   <item>nothing is stored until a person commits — the screen says so in words;</item>
///   <item>every value carries its page and its confidence, and a value that was OCR'd is
///         <i>erkannt</i>, not <i>gelesen</i>;</item>
///   <item>a guarantee and a projection live in separate blocks, and no projected figure is rendered
///         next to the balance.</item>
/// </list>
///
/// All three are single lines of markup that a refactor can drop without anything failing — there is no
/// browser test in this repo — so they are pinned here, the way PensionUxBaselineTests pins the manual
/// flow's rules.
/// </summary>
public sealed class PensionDocumentUxBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;

    public PensionDocumentUxBaselineTests(FullWorthWebFactory factory) => _factory = factory;

    /// <summary>
    /// The module has to be served, mounted as the fourth tab with its own route, and precached —
    /// pension.js imports it statically, so an installed PWA that cold-starts offline fails on that
    /// import if `sw.js` does not list it.
    /// </summary>
    [Fact]
    public void DocumentsTabIsServedRegisteredAndPrecached()
    {
        var pension = ReadAsset("features", "pension.js");
        var sw = ReadAsset("sw.js");

        Assert.Contains("from './pension-documents.js'", pension);
        Assert.Contains("renderPensionDocuments", pension);
        Assert.Contains("tabDocuments: 'Dokumente'", pension);
        Assert.Contains("path: '/pension/dokumente'", pension);
        Assert.Contains("data-pension-documents", pension);

        Assert.Contains("/features/pension-documents.js", sw);

        // Flat in features/, never a features/<name>/ subfolder (CLAUDE.md).
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.True(File.Exists(Path.Combine(environment.WebRootPath, "features", "pension-documents.js")));
        Assert.False(Directory.Exists(Path.Combine(environment.WebRootPath, "features", "pension-documents")));
    }

    /// <summary>
    /// Rule 1. A commit is the only thing that writes, and the screen has to say that in plain words
    /// before anyone edits 25 fields: an extraction that silently stored its guesses is the failure
    /// mode this whole step exists to prevent.
    /// </summary>
    [Fact]
    public void ReviewScreenSaysNothingIsStoredUntilTheCommit()
    {
        var js = ReadAsset("features", "pension-documents.js");

        Assert.Contains("Noch ist nichts gespeichert.", js);
        Assert.Contains("pension-doc-unsaved", js);
        // The commit is a POST to /commit and it is the only call that creates rows; the review PUT
        // only remembers the draft, and the copy has to distinguish the two.
        Assert.Contains("/commit", js);
        Assert.Contains("/review", js);
        Assert.Contains("commitConfirm", js);
    }

    /// <summary>
    /// Rule 2. Page and confidence per field come out of `provenance`, an unresolved field is rendered
    /// EMPTY and asks instead of showing a guess, and OCR is marked on the field rather than in a
    /// footnote — a recognised number that looks like a read one is the trap here.
    /// </summary>
    [Fact]
    public void EveryFieldCarriesItsPageConfidenceAndWhetherItWasOnlyRecognised()
    {
        var js = ReadAsset("features", "pension-documents.js");
        var css = ReadAsset("styles", "features", "pension.css");

        Assert.Contains("provenance", js);
        Assert.Contains("matchedLabel", js);
        Assert.Contains("Seite {page}", js);
        Assert.Contains("Sicherheit {percent} %", js);

        // Read vs recognised, and the AI pass says so too.
        Assert.Contains("textLayerUsed === false", js);
        Assert.Contains("recognised: 'erkannt'", js);
        Assert.Contains("read: 'gelesen'", js);
        Assert.Contains("pension-doc-field-ocr", js);
        Assert.Contains("pension-doc-flag-ocr", js);
        Assert.Contains("aiBadge", js);

        // An unresolved field is empty and asking — never pre-filled with a guess.
        Assert.Contains("isUnresolved", js);
        Assert.Contains("if (isUnresolved(path)) return null;", js);
        Assert.Contains("pension-doc-field-unresolved", js);

        // The states have to be visible, so the classes must actually exist in the stylesheet.
        Assert.Contains(".pension-doc-field-ocr", css);
        Assert.Contains(".pension-doc-field-unresolved", css);
        Assert.Contains(".pension-doc-flag", css);
    }

    /// <summary>
    /// Rule 3. `CK_BavSnapshots_Projection` refuses a projection without its assumption in the
    /// database; the screen must not undo that by rendering a projected figure next to the balance.
    /// The three blocks are separate elements with separate classes, and the projection names its
    /// return.
    /// </summary>
    [Fact]
    public void GuaranteeAndProjectionAreSeparateBlocksAndTheProjectionNamesItsAssumption()
    {
        var js = ReadAsset("features", "pension-documents.js");
        var css = ReadAsset("styles", "features", "pension.css");

        Assert.Contains("pension-doc-now", js);
        Assert.Contains("pension-doc-guarantee", js);
        Assert.Contains("pension-doc-projection", js);
        Assert.Contains(".pension-doc-guarantee", css);
        Assert.Contains(".pension-doc-projection", css);

        // Money that exists today and the projection are rendered from two different specs, and no
        // projected field may appear in the snapshot block.
        var snapshotSpec = Between(js, "const SNAPSHOT_SPEC = [", "];");
        Assert.DoesNotContain("project", snapshotSpec, StringComparison.OrdinalIgnoreCase);
        var guaranteeSpec = Between(js, "const GUARANTEE_SPEC = [", "];");
        Assert.DoesNotContain("project", guaranteeSpec, StringComparison.OrdinalIgnoreCase);

        var projectionSpec = Between(js, "const PROJECTION_SPEC = [", "];");
        Assert.Contains("snapshot.projectionReturnPercent", projectionSpec);
        Assert.Contains("snapshot.projectionBasis", projectionSpec);
        Assert.DoesNotContain("snapshot.balance", projectionSpec);

        // …and it is labelled as an assumption, not as a figure anybody was promised.
        Assert.Contains("eine Annahme", js);
        Assert.Contains("keine Garantie", js);
    }

    /// <summary>
    /// The employee / Arbeitgeberzuschuss / Arbeitgeberanteil split stays three amounts, and a printed
    /// total that disagrees is shown next to them. Summing them into one number would erase the one
    /// distinction that decides whether an amount is an outflow (docs/PENSION.md, "Money direction").
    /// </summary>
    [Fact]
    public void TheThreeContributionSharesStaySeparateAndAPrintedTotalIsNeverCorrected()
    {
        var js = ReadAsset("features", "pension-documents.js");

        var spec = Between(js, "const CONTRIBUTION_SPEC = [", "];");
        Assert.Contains("contribution.employeeAmount", spec);
        Assert.Contains("contribution.employerSubsidyAmount", spec);
        Assert.Contains("contribution.employerAmount", spec);
        Assert.Contains("contribution.statedTotalAmount", spec);

        Assert.Contains("statedMismatch", js);
        Assert.Contains("nicht korrigiert", js);
        // No tax or social-security effect is offered here at all: BavContributionDraft deliberately
        // has no such field, so an extractor cannot invent one and neither can this screen.
        Assert.DoesNotContain("taxSavingAmount", js);
        Assert.DoesNotContain("socialSecuritySavingAmount", js);
        Assert.DoesNotContain("netEffortAmount", js);
    }

    /// <summary>
    /// A match names the rule that fired and is confirmed by a person; only an unmatched document may
    /// create a contract. Applying a weak match silently is how two contracts become one.
    /// </summary>
    [Fact]
    public void AMatchNamesItsRuleAndOnlyAnUnmatchedDocumentOffersANewContract()
    {
        var js = ReadAsset("features", "pension-documents.js");

        Assert.Contains("matchedOn", js);
        Assert.Contains("policy_number", js);
        Assert.Contains("provider_tariff_employer", js);
        Assert.Contains("provider_retirement_date", js);
        Assert.Contains("diesem Vertrag zuordnen", js);
        Assert.Contains("neuen Vertrag anlegen", js);
        // The commit button starts disabled, so nothing can be committed without a chosen assignment.
        Assert.Contains("commit.disabled = true;", js);
    }

    /// <summary>
    /// The commit answer is reported honestly: a skipped snapshot is useful information, not a failure,
    /// and a token this module does not know is shown as it came rather than relabelled into something
    /// that reads better than what happened.
    /// </summary>
    [Fact]
    public void TheCommitResultShowsAppliedAndSkippedHonestly()
    {
        var js = ReadAsset("features", "pension-documents.js");

        Assert.Contains("applied", js);
        Assert.Contains("skipped", js);
        Assert.Contains("bereits vorhanden", js);
        Assert.Contains("Übersprungen heißt nicht fehlgeschlagen", js);
        Assert.Contains("if (!known) return raw;", js);
    }

    /// <summary>
    /// A 12 MB cap and a duplicate both have an answer that leads somewhere, and the original file is
    /// reached through a link — never fetched into the page. Pulling a stored document's bytes into an
    /// &lt;img&gt; or a warm-up fetch would copy an encrypted contract document into the browser cache
    /// for no benefit at all.
    /// </summary>
    [Fact]
    public void UploadHasACapAndTheOriginalIsLinkedNeverFetched()
    {
        var js = ReadAsset("features", "pension-documents.js");

        Assert.Contains("12 * 1024 * 1024", js);
        Assert.Contains("existingDocumentId", js);
        Assert.Contains("zum vorhandenen Dokument", js);
        Assert.Contains("new FormData()", js);
        Assert.Contains("body.append('document'", js);

        // The content endpoint appears exactly once, as the href of that link — never in a ctx.api()
        // call, an <img> or a cache warm-up.
        Assert.Equal(1, js.Split("/content`").Length - 1);
        Assert.Contains("ctx.bffUrl(`api/pension/documents/${document_.id}/content`)", js);
        Assert.Contains("target=\"_blank\" rel=\"noopener\"", js);
        Assert.DoesNotContain("new Image(", js);
        Assert.DoesNotContain("force-cache", js);
        Assert.DoesNotContain("caches.open", js);
    }

    /// <summary>
    /// The review screen is a PAGE. docs/UI_AUDIT.md measured the dialogs as this app's main usability
    /// problem, and this form has more controls than the worst call site it lists — so the only dialog
    /// in the flow is the one-sentence commit confirmation, which comes from the shared confirm helper.
    /// </summary>
    [Fact]
    public void TheReviewScreenIsAPageAndNotADialog()
    {
        var js = ReadAsset("features", "pension-documents.js");

        Assert.Contains("pension-doc-screen", js);
        Assert.Contains("data-doc-back", js);
        // ctx.confirm is the shared one-sentence/two-button dialog; nothing here builds its own.
        Assert.Contains("ctx.confirm(", js);
        Assert.DoesNotContain("ctx.dialog(", js);
        Assert.DoesNotContain("showModal", js);
        Assert.DoesNotContain("dialog-card", js);
    }

    /// <summary>
    /// Shared infrastructure only, and no debug leftovers: money and dates come from the app context,
    /// the BFF is reached through it too, and there is no framework, no DOM-patch observer and no
    /// inline &lt;style&gt; (the CSP blocks a style block anyway).
    /// </summary>
    [Fact]
    public void TheModuleReusesTheSharedInfrastructureAndLeavesNoDebugCode()
    {
        var js = ReadAsset("features", "pension-documents.js");

        Assert.Contains("ctx.money(", js);
        Assert.Contains("ctx.date(", js);
        Assert.Contains("ctx.jsonBody(", js);
        Assert.Contains("bffUrl(", js);
        Assert.Contains("from '../ui/ux-kit.js'", js);

        Assert.DoesNotContain("/bff/", js);
        Assert.DoesNotContain("Intl.NumberFormat", js);
        Assert.DoesNotContain("new MutationObserver", js);
        Assert.DoesNotContain("<style", js);
        Assert.DoesNotContain("console.log", js);
        Assert.DoesNotContain("debugger", js);
    }

    /// <summary>
    /// Tokens only, and the grid declaration the dialog overflow in docs/UI_AUDIT.md came down to:
    /// `minmax(0, 1fr)` plus `min-width: 0`. Plain `1fr` would not do it, because a grid item's default
    /// `min-width: auto` refuses to shrink below an input's min-content width.
    /// </summary>
    [Fact]
    public void DocumentStylesUseTokensAndCanShrinkOnAPhone()
    {
        var css = ReadAsset("styles", "features", "pension.css");

        Assert.Contains(".pension-doc-drop", css);
        Assert.Contains(".pension-doc-block", css);
        Assert.Contains(".pension-doc-grid", css);
        Assert.Contains(".pension-doc-row", css);
        Assert.Contains(".pension-doc-unsaved", css);
        Assert.Contains(".pension-doc-actions", css);

        Assert.Contains("minmax(0, 1fr)", css);
        Assert.Contains("min-width: 0", css);
        Assert.Contains("var(--surface", css);
        Assert.Contains("var(--line)", css);
        // No hardcoded colour anywhere in this stylesheet — the same guard PensionUxBaselineTests has.
        Assert.DoesNotContain("#", css);
        Assert.DoesNotContain("rgb(", css);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' is gone from features/pension-documents.js");
        from += start.Length;
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{start}' is no longer terminated by '{end}'");
        return source[from..to];
    }

    private string ReadAsset(params string[] path)
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
