using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// Step 3 of docs/PENSION.md on the two screens that show other people's money next to it: the wealth
/// page and the dashboard. Both rules here are the kind a refactor breaks silently — the tied figure
/// starts being derived on the client again, or a projected annuity ends up in the same line as a
/// balance and reads as money somebody has today.
/// </summary>
public sealed class PensionWealthUiBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;

    public PensionWealthUiBaselineTests(FullWorthWebFactory factory) => _factory = factory;

    /// <summary>
    /// The slice and the "davon gebunden" line both read the backend component, which was converted
    /// with the same rates as the total. Deriving either from the raw asset list would reintroduce the
    /// cross-currency ratio bug the real-estate slice was built to fix.
    /// </summary>
    [Fact]
    public void WealthPageReadsThePensionComponentAndNamesTheTiedPart()
    {
        var js = ReadAsset("features", "networth.js");

        Assert.Contains("overview.pensionAssets?.amount", js);
        Assert.Contains("pensionAssets: 'Altersvorsorge'", js);
        Assert.Contains("data-tied-wealth", js);
        Assert.Contains("tiedLabel", js);
        Assert.Contains("tiedNote", js);
        // In words, not just a number: the point of the line is that this money is not available yet.
        Assert.Contains("Rentenbeginn", js);
        Assert.Contains("not available", js);
        // Never a "0,00 € gebunden" row.
        Assert.Contains("if (tied <= 0.005) return '';", js);
        // The allocation slice is a palette token, never a colour.
        Assert.Contains("t('pensionAssets'), amount: pension, color: 'var(--cat-5)'", js);
    }

    /// <summary>
    /// Guarantee and projection are separate columns from the database up, so no screen may merge
    /// them. The balance line carries the balance and nothing else.
    /// </summary>
    [Fact]
    public void DashboardPensionWidgetKeepsTheBalanceApartFromTheProjection()
    {
        var js = ReadAsset("ui", "dashboard.js");

        Assert.Contains("api/pension/overview", js);
        Assert.Contains("o.totalBalance", js);
        Assert.Contains("o.guaranteedMonthlyAnnuity", js);
        Assert.Contains("o.projectedMonthlyAnnuity", js);
        // Both annuities are labelled, and the projection says what it is not.
        Assert.Contains("guaranteed: 'Garantierte Rente / Monat'", js);
        Assert.Contains("projected: 'Prognose Rente / Monat'", js);
        Assert.Contains("projectionNote", js);
        Assert.Contains("keine Garantie", js);

        // The figure rendered big, as money today, is the balance alone.
        var metric = Regex.Match(js, @"widget-metric dash-metric""><strong>(?<inner>.*?)</strong>",
            RegexOptions.Singleline);
        while (metric.Success)
        {
            var inner = metric.Groups["inner"].Value;
            if (inner.Contains("totalBalance", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("projected", inner);
                Assert.DoesNotContain("guaranteed", inner);
            }
            metric = metric.NextMatch();
        }

        // An incomplete overview says so instead of printing a confident total that is short.
        Assert.Contains("o.isComplete === false", js);
        Assert.Contains("o.missingCurrencies", js);
        Assert.Contains("fx-incomplete", js);
    }

    private string ReadAsset(params string[] path)
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }
}
