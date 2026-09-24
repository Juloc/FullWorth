using FullWorth.Web.Navigation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// The Altersvorsorge area has to be reachable and offline-cacheable like every other feature: a view
/// section in the shell, a nav entry, a route in the registry, and both assets in the service-worker
/// shell list. A feature that renders correctly but is missing from `sw.js` breaks only after the PWA
/// is installed, which is exactly when nobody is looking.
/// </summary>
public sealed class PensionUxBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;

    public PensionUxBaselineTests(FullWorthWebFactory factory) => _factory = factory;

    [Fact]
    public void PensionAreaIsWiredIntoTheShellAndThePwaCache()
    {
        // Seit #154 ist der Bereich eine eigene Razor-Seite statt einer Ansicht in der einen Hülle.
        // Die Zusicherung ist dieselbe geblieben - er ist vollständig verdrahtet - nur die Orte haben
        // gewechselt: das Markup liegt bei der Seite, der Einstieg in entry.js, und app/routes.js muss
        // ihn führen, sonst fängt die alte Hülle seine Links weiter ab.
        var page = WebSources.Page("Pension");
        var entry = ReadAsset("pages", "pension", "entry.js");
        var sw = ReadAsset("sw.js");

        Assert.Contains("@page \"/pension\"", page);
        Assert.Contains("id=\"view-pension\"", page);
        Assert.Contains("/pages/pension/page.css", page);
        Assert.Contains("/pages/pension/entry.js", page);

        Assert.Contains("./page.js", entry);
        Assert.Contains("bindPension(context)", entry);
        Assert.Contains("renderPension(context)", entry);

        // "Erreichbar" hiess bis #154: die Huelle kennt die Ansicht. Jetzt heisst es: der Katalog
        // kennt sie, und es gibt eine Razor-Seite dafuer.
        Assert.Contains(NavigationCatalog.Entries, entry => entry.View == "pension");

        Assert.Contains("/pages/pension/page.js", sw);
        Assert.Contains("/pages/pension/page.css", sw);
    }

    /// <summary>
    /// The area's own rules have to survive a refactor of the module, because they are the reason the
    /// screen is trustworthy: a projection is labelled and separated from the balance, "beitragsfrei"
    /// is its own status rather than a variant of terminated, an estimate says it is one, and the
    /// employer share is described as a benefit instead of an expense.
    /// </summary>
    [Fact]
    public void PensionModuleKeepsProjectionsAndBeitragsfreiHonest()
    {
        var js = ReadAsset("pages", "pension", "page.js");

        Assert.Contains("projectionIsSimulation", js);
        Assert.Contains("projectionHint", js);
        Assert.Contains("pension-value-projected", js);
        Assert.Contains("paid_up", js);
        Assert.Contains("continuesWhenPaidUp", js);
        Assert.Contains("estimateBadge", js);
        Assert.Contains("employerHint", js);
        // A tax effect is only ever sent with the source that stated it.
        Assert.Contains("taxEffectSource", js);
        // A missing FX rate is surfaced, never treated as 1:1.
        Assert.Contains("missingCurrencies", js);
        Assert.Contains("unconvertedBalances", js);

        // Shared infrastructure only: no direct BFF URL, no framework, no DOM-patch observer.
        Assert.DoesNotContain("/bff/", js);
        Assert.DoesNotContain("new MutationObserver", js);
        Assert.DoesNotContain("<style", js);
    }

    /// <summary>Tokens only, and nothing that can overflow a ~380 px viewport.</summary>
    [Fact]
    public void PensionStylesUseTokensAndCollapseOnNarrowViewports()
    {
        var css = ReadAsset("pages", "pension", "page.css");

        Assert.Contains("@media (max-width: 760px)", css);
        Assert.Contains("var(--surface", css);
        Assert.Contains("var(--line)", css);
        Assert.DoesNotContain("#", css);
        Assert.DoesNotContain("rgb(", css);
    }

    private string ReadAsset(params string[] path)
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
