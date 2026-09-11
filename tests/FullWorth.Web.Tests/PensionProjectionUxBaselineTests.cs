using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// The Simulation tab (step 3 of docs/PENSION.md) computes a projection and compares two contracts.
/// Four of its properties are promises to the user rather than implementation details, and each one is
/// a line of markup that a refactor can drop without anything failing — there is no browser test in
/// this repo, so they are pinned here the way PensionUxBaselineTests pins the manual flow:
///
/// <list type="number">
///   <item>a projection is never presented as a value: the guaranteed and the projected figures get
///         separate blocks, and every projected figure carries its <c>returnPercent</c>;</item>
///   <item>a zero delta is stated in words — splitting a contribution across two contracts produces no
///         extra compound interest — because a bare "0,00 €" teaches nobody that;</item>
///   <item>a non-zero delta is attributed through <c>cause</c>, and <c>different_assumptions</c> says
///         the two sides are not comparable as contracts at all;</item>
///   <item>an excluded contract is named with its blocker and with what unblocks it: a total that
///         silently drops a contract looks complete and is not.</item>
/// </list>
/// </summary>
public sealed class PensionProjectionUxBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;

    public PensionProjectionUxBaselineTests(FullWorthWebFactory factory) => _factory = factory;

    /// <summary>
    /// The tab has to exist, carry its own route so Back and Forward work between the tabs, be mounted
    /// by pension.js, and be precached — pension.js imports it statically, so an installed PWA that
    /// cold-starts offline fails on that import if `sw.js` does not list it.
    /// </summary>
    [Fact]
    public void SimulationTabIsServedRegisteredAndPrecached()
    {
        var pension = ReadAsset("features", "pension.js");
        var sw = ReadAsset("sw.js");

        Assert.Contains("from './pension-projection.js'", pension);
        Assert.Contains("renderPensionProjection", pension);
        Assert.Contains("tabSimulation: 'Simulation'", pension);
        Assert.Contains("path: '/pension/simulation'", pension);
        Assert.Contains("data-pension-simulation", pension);
        // Leaving the tab drops the result: a projection belongs to the scenario that produced it.
        Assert.Contains("resetPensionProjection()", pension);
        // The copy and the number formatting are handed in rather than imported, so the pair can never
        // become a circular import and the two halves of one screen cannot print a percentage two
        // different ways.
        Assert.Contains("percent, reload: () => renderPension(ctx)", pension);

        Assert.Contains("/features/pension-projection.js", sw);

        // Flat in features/, never a features/<name>/ subfolder (CLAUDE.md).
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.True(File.Exists(Path.Combine(environment.WebRootPath, "features", "pension-projection.js")));
        Assert.False(Directory.Exists(Path.Combine(environment.WebRootPath, "features", "pension-projection")));
    }

    /// <summary>
    /// Rule 1, the reason the whole feature exists. What was promised and what is merely assumed are two
    /// separate blocks with two separate classes, and neither block may carry the other's figures — a
    /// projected number rendered next to a balance is what <c>CK_BavSnapshots_Projection</c> refuses in
    /// the database, and the screen must not undo it.
    /// </summary>
    [Fact]
    public void GuaranteeAndProjectionAreSeparateBlocks()
    {
        var js = ReadAsset("features", "pension-projection.js");
        var css = ReadAsset("styles", "features", "pension.css");

        Assert.Contains("pension-sim-guarantee", js);
        Assert.Contains("pension-sim-projection", js);
        // The projection reuses the shared dashed edge, so a reader learns the "different kind of
        // number" affordance once across the whole area.
        Assert.Contains("pension-value-projected pension-sim-projection", js);
        Assert.Contains(".pension-sim-guarantee", css);
        Assert.Contains(".pension-sim-projection", css);

        // The totals: the guarantee block holds no projected figure and the projection block holds no
        // guaranteed one. Two <section> siblings, never rows of one table.
        var guarantee = Between(js, "pension-sim-guarantee\">", "</section>");
        Assert.Contains("totalGuaranteedCapital", guarantee);
        Assert.Contains("totalGuaranteedMonthlyAnnuity", guarantee);
        Assert.DoesNotContain("project", guarantee, StringComparison.OrdinalIgnoreCase);

        var projection = Between(js, "pension-value-projected pension-sim-projection\">", "</section>");
        Assert.Contains("totalProjectedCapital", projection);
        Assert.Contains("totalProjectedMonthlyAnnuity", projection);
        Assert.DoesNotContain("totalGuaranteed", projection);
        Assert.DoesNotContain("totalCurrentBalance", projection);

        // …and it is labelled as an assumption, not as a figure anybody was promised.
        Assert.Contains("eine Annahme", js);
        Assert.Contains("keine Garantie", js);
    }

    /// <summary>
    /// Still rule 1: a projected figure without its assumption is indistinguishable from a guarantee,
    /// so EVERY projection block carries the return badge. Counting them is what keeps a fourth block
    /// from being added later without one.
    /// </summary>
    [Fact]
    public void EveryProjectedFigureNamesItsReturn()
    {
        var js = ReadAsset("features", "pension-projection.js");

        Assert.Contains("withReturn: '{percent} % p. a. angenommen'", js);
        Assert.Contains("returnPercent", js);

        var blocks = Occurrences(js, "pension-sim-projection\">");
        var badges = Occurrences(js, "pension-sim-return-badge");
        Assert.True(blocks >= 3, $"expected the totals, the per-contract and the comparison projection block, found {blocks}");
        Assert.Equal(blocks, badges);

        // The per-contract block uses that contract's own return, not the request's, so a contract the
        // server answered differently cannot be relabelled by the screen.
        Assert.Contains("percent: percent(item.returnPercent)", js);
    }

    /// <summary>
    /// `projectedMonthlyAnnuity` is null when the contract states no annuity factor. The capital is
    /// shown and the gap is explained: blanking the row would hide a projection the user asked for, and
    /// filling it with arithmetic of our own would be exactly the invention rule 1 forbids.
    /// </summary>
    [Fact]
    public void AMissingAnnuityFactorIsExplainedRatherThanBlankedOrInvented()
    {
        var js = ReadAsset("features", "pension-projection.js");

        Assert.Contains("item.projectedMonthlyAnnuity == null", js);
        Assert.Contains("noAnnuity:", js);
        Assert.Contains("noAnnuityHint:", js);
        Assert.Contains("noAnnuityFix:", js);
        Assert.Contains("Der Vertrag nennt keinen Rentenfaktor.", js);
        // No annuity is derived here: the factor arithmetic belongs to the server, which refuses to do
        // it without a stated factor.
        Assert.DoesNotContain("annuityFactor *", js);
        Assert.DoesNotContain("/ 10000", js);
    }

    /// <summary>
    /// Rule 2, and the point of the comparison. With the same money under the same assumptions the
    /// capital delta is zero, and the screen has to SAY so: splitting a contribution across two
    /// contracts produces no extra compound interest. A bare "0,00 €" is the failure here.
    /// </summary>
    [Fact]
    public void AZeroDeltaIsStatedInWordsAndNotAsAnAmount()
    {
        var js = ReadAsset("features", "pension-projection.js");
        var css = ReadAsset("styles", "features", "pension.css");

        Assert.Contains("sameMoneySameAssumptions", js);
        Assert.Contains("erzeugt keinen zusätzlichen Zinseszins", js);
        Assert.Contains("50 € + 288 € sind genau 338 €", js);
        Assert.Contains("pension-sim-same-money", js);
        Assert.Contains(".pension-sim-same-money", css);

        // The sentence replaces the delta rows rather than sitting next to them: the branch is on
        // sameMoneySameAssumptions, and the amounts are the other arm of it.
        var verdict = Between(js, "const verdict = comparison.sameMoneySameAssumptions", "// Rule 3");
        var sentenceAt = verdict.IndexOf("sameMoneySentence", StringComparison.Ordinal);
        var deltaAt = verdict.IndexOf("capitalDelta", StringComparison.Ordinal);
        Assert.True(sentenceAt >= 0 && deltaAt > sentenceAt,
            "the zero-delta sentence has to be the first arm of the branch and the amounts the second");
    }

    /// <summary>
    /// Rule 3. A delta the screen cannot attribute is the invented advantage the server refuses to
    /// produce, so `cause` is rendered as the explanation; and two sides on different returns are not
    /// comparable as contracts at all, which the screen says instead of showing the difference as if it
    /// came from the contracts.
    /// </summary>
    [Fact]
    public void ANonZeroDeltaIsAttributedAndDifferentAssumptionsAreNotComparable()
    {
        var js = ReadAsset("features", "pension-projection.js");

        Assert.Contains("comparison.cause", js);
        Assert.Contains("data-sim-cause", js);

        var causes = Between(js, "    causes: {", "    },");
        Assert.Contains("costs:", causes);
        Assert.Contains("guarantee:", causes);
        Assert.Contains("investment_concept:", causes);
        Assert.Contains("none:", causes);
        Assert.Contains("different_assumptions:", causes);

        Assert.Contains("causes.includes('different_assumptions')", js);
        Assert.Contains("notComparable", js);
        Assert.Contains("nicht vergleichbar", js);
        // An unknown token is shown as it came rather than relabelled into something that reads better
        // than what actually happened.
        Assert.Contains("d().causes[cause] || cause", js);
    }

    /// <summary>
    /// An excluded contract is named with its blocker AND with the one thing that unblocks it. A
    /// silently missing contract is the worst outcome on this screen, because the totals then look
    /// complete while they are short a whole contract.
    /// </summary>
    [Fact]
    public void EveryExcludedContractIsNamedWithItsBlockerAndItsRemedy()
    {
        var js = ReadAsset("features", "pension-projection.js");

        Assert.Contains("result.excluded", js);
        Assert.Contains("data-sim-blocker", js);
        Assert.Contains("excludedHint", js);

        var blockers = Between(js, "    blockers: {", "    },");
        var fixes = Between(js, "    blockerFixes: {", "    },");
        foreach (var blocker in new[] { "no_retirement_date", "no_balance", "already_due", "missing_rate" })
        {
            Assert.Contains(blocker, blockers);
            Assert.Contains(blocker, fixes);
        }

        // A blocker token this module does not know still reaches the screen as itself.
        Assert.Contains("d().blockers[blocker] || blocker", js);

        // An incomplete total gets the same calm treatment as everywhere else in the app: the shared
        // notice, the currencies named, and never a 1:1 assumption.
        Assert.Contains("result.isComplete === false", js);
        Assert.Contains("result.missingCurrencies", js);
        Assert.Contains("pension-notice pension-notice-warn", js);
    }

    /// <summary>
    /// The scenario form is INLINE on the tab. A return assumption is changed repeatedly, and
    /// re-opening a dialog for every change is the friction docs/UI_AUDIT.md spent six steps removing —
    /// so there is no dialog in this flow at all. And nothing here writes: the module only ever POSTs
    /// the two read-only projection endpoints, so a scenario the user tried cannot end up in net worth.
    /// </summary>
    [Fact]
    public void TheScenarioFormIsInlineAndTheTabNeverWritesAnything()
    {
        var js = ReadAsset("features", "pension-projection.js");

        // 3 / 5 / 7 % plus a free field, per the brief.
        Assert.Contains("const RETURN_PRESETS = [3, 5, 7];", js);
        Assert.Contains("data-sim-preset=\"custom\"", js);
        Assert.Contains("data-sim-return", js);
        Assert.Contains("continueContributions", js);
        // An empty retirement date means "each contract's own date" and is left out of the request —
        // never sent as today, never as somebody else's retirement date.
        Assert.Contains("if (scenario.retirementDate) request.retirementDate", js);

        Assert.DoesNotContain("ctx.dialog(", js);
        Assert.DoesNotContain("showModal", js);
        Assert.DoesNotContain("dialog-card", js);

        Assert.Equal(2, Occurrences(js, "ctx.api("));
        Assert.Contains("ctx.api('api/pension/projection', ctx.jsonBody(", js);
        Assert.Contains("ctx.api('api/pension/projection/compare', ctx.jsonBody(", js);
        Assert.DoesNotContain("'PUT'", js);
        Assert.DoesNotContain("'DELETE'", js);
    }

    /// <summary>
    /// Shared infrastructure only, and no debug leftovers: money and dates come from the app context,
    /// the percent formatter from pension.js, and there is no framework, no DOM-patch observer and no
    /// inline &lt;style&gt; (the CSP blocks a style block anyway).
    /// </summary>
    [Fact]
    public void TheModuleReusesTheSharedInfrastructureAndLeavesNoDebugCode()
    {
        var js = ReadAsset("features", "pension-projection.js");

        Assert.Contains("ctx.money(", js);
        Assert.Contains("ctx.date(", js);
        Assert.Contains("ctx.jsonBody(", js);
        Assert.Contains("from '../ui/ux-kit.js'", js);
        // Buttons are the shared roles, never hand-rolled.
        Assert.Contains("btn btn-primary", js);
        Assert.Contains("btn btn-secondary", js);

        Assert.DoesNotContain("/bff/", js);
        Assert.DoesNotContain("Intl.NumberFormat", js);
        Assert.DoesNotContain("new MutationObserver", js);
        Assert.DoesNotContain("<style", js);
        Assert.DoesNotContain("console.log", js);
        Assert.DoesNotContain("debugger", js);
    }

    /// <summary>
    /// Tokens only, the grid declaration the dialog overflow in docs/UI_AUDIT.md came down to, and
    /// `dvh` rather than `vh`: `vh` is the LARGEST viewport, so a height in `vh` reaches under the
    /// browser chrome on a phone.
    /// </summary>
    [Fact]
    public void SimulationStylesUseTokensAndCanShrinkOnAPhone()
    {
        var css = ReadAsset("styles", "features", "pension.css");

        Assert.Contains(".pension-simulation", css);
        Assert.Contains(".pension-sim-scenario", css);
        Assert.Contains(".pension-sim-blocks", css);
        Assert.Contains(".pension-sim-compare-form", css);
        Assert.Contains(".pension-sim-sides", css);
        Assert.Contains(".pension-sim-cause-list", css);

        // The phone collapse is minmax(0, 1fr) and not 1fr: a grid item's default min-width is auto, so
        // plain 1fr refuses to shrink below an input's min-content width.
        Assert.Contains(".pension-sim-compare-form, .pension-sim-sides { grid-template-columns: minmax(0, 1fr); }", css);

        Assert.Contains("var(--surface", css);
        Assert.Contains("var(--line)", css);
        // No hardcoded colour anywhere in this stylesheet — the same guard the other two pension tests
        // carry, kept here so a simulation rule cannot be the one that breaks it.
        Assert.DoesNotContain("#", css);
        Assert.DoesNotContain("rgb(", css);
        // Every viewport height in this file is a dynamic one.
        Assert.Equal(Occurrences(css, "dvh"), Occurrences(css, "vh"));
    }

    private static int Occurrences(string source, string needle)
        => source.Split(needle).Length - 1;

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' is gone from features/pension-projection.js");
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
