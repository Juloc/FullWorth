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
        var html = ReadAsset("index.html");
        var app = ReadAsset("app.js");
        var sw = ReadAsset("sw.js");

        Assert.Contains("id=\"view-pension\"", html);
        Assert.Contains("data-view=\"pension\"", html);
        Assert.Contains("/styles/features/pension.css", html);

        Assert.Contains("from './features/pension.js'", app);
        Assert.Contains("register('pension'", app);
        Assert.Contains("'pension'", app);
        Assert.Contains("bindPension(ctx)", app);

        Assert.Contains("/features/pension.js", sw);
        Assert.Contains("/styles/features/pension.css", sw);
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
        var js = ReadAsset("features", "pension.js");

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
        var css = ReadAsset("styles", "features", "pension.css");

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
