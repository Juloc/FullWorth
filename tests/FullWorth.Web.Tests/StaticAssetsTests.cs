using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace FullWorth.Web.Tests;

/// <summary>
/// Statische Dateien kommen ueber MapStaticAssets (#154) - und ohne Anmeldung.
///
/// Das Zweite ist die Falle: UseStaticFiles lief als Middleware VOR der Autorisierung, MapStaticAssets
/// sind Endpunkte, und die FallbackPolicy verlangt fuer jeden Endpunkt eine Anmeldung. Ohne ein
/// ausdrueckliches AllowAnonymous braeuchte das Stylesheet der Anmeldeseite eine Anmeldung - niemand
/// kaeme mehr hinein, und kein bisheriger Test haette es bemerkt, weil keiner anonym eine Datei holte.
/// </summary>
public sealed class StaticAssetsTests(FullWorthWebFactory factory) : IClassFixture<FullWorthWebFactory>
{
    [Theory]
    [InlineData("/styles/tokens.css", "text/css")]
    [InlineData("/pages/pension/entry.js", "text/javascript")]
    [InlineData("/sw.js", "text/javascript")]
    public async Task AnAnonymousVisitorGetsTheFiles(string path, string mediaType)
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Was MapStaticAssets bringt und UseStaticFiles nicht: eine Cache-Anweisung und einen ETag. Ohne
    /// Cache-Control durfte der Browser eine Datei nach eigenem Ermessen weiterverwenden - nach einem
    /// Release also altes JS zu neuem HTML reichen.
    /// </summary>
    [Fact]
    public async Task EveryFileSaysHowLongItMayBeKept()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/styles/tokens.css");

        response.EnsureSuccessStatusCode();
        Assert.NotNull(response.Headers.CacheControl);
        Assert.NotNull(response.Headers.ETag);
    }
}

/// <summary>
/// Der Schalter fuer einen Stack, der wwwroot live ins Image bindet (Finance/local): dort liest der
/// Server von der Platte, weil Groesse und Fingerabdruck aus dem Build nach jeder Bearbeitung falsch
/// waeren. Woran man erkennt, dass er greift: die Platte schickt keine Cache-Anweisung mit.
/// </summary>
public sealed class LiveStaticFilesTests(LiveStaticFilesTests.Factory factory) : IClassFixture<LiveStaticFilesTests.Factory>
{
    public sealed class Factory : FullWorthWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FullWorthWeb:LiveStaticFiles", "true");
        }
    }

    [Fact]
    public async Task TheFileComesFromDiskAndStillWithoutALogin()
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/styles/tokens.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
    }
}
