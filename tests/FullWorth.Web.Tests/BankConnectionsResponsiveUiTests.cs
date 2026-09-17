using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class BankConnectionsResponsiveUiTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;

    public BankConnectionsResponsiveUiTests(FullWorthWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void MobileConnectionRows_StackContentAndActionsInsteadOfSqueezingTheTextColumn()
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var css = File.ReadAllText(Path.Combine(
            environment.WebRootPath,
            "pages",
            "settings",
            "bank-connections",
            "page.css"));

        Assert.Contains("@media(max-width:767px)", css);
        Assert.Contains("#view-bank-connections .row{grid-template-columns:minmax(0,1fr);align-items:stretch}", css);
        Assert.Contains("#view-bank-connections .row-side{width:100%;justify-content:flex-start}", css);
        Assert.Contains("#view-bank-connections .row-side>.amount{flex:1 0 100%}", css);
        Assert.Contains("#view-bank-connections .row-side>.btn-secondary{flex:1 1 10rem", css);
        Assert.Contains("#view-bank-connections .row-sub{overflow-wrap:break-word;word-break:normal}", css);
    }
}
