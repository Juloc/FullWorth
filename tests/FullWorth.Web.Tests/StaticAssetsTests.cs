using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
    /// Die gerenderte Seite verweist auf ihre Dateien per Fingerabdruck ("~/" im Markup, #154). Das
    /// ist, was ein Release sicher macht: neues HTML nennt neue Adressen, und eine alte Datei aus dem
    /// Cache passt zu keiner davon.
    ///
    /// Die eine Ausnahme wird mitgeprueft: der Schrift-Preload. Das Stylesheet laedt die Schrift unter
    /// ihrem Namen; bekaeme der Preload den Fingerabdruck, laede der Browser sie zweimal.
    /// </summary>
    [Fact]
    public async Task ARenderedPageLinksItsFilesByFingerprint()
    {
        const string password = "correct horse battery staple";
        var email = $"fingerprint-{Guid.NewGuid():N}@example.com";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<FullWorth.Web.Modules.Auth.AuthService>();
            Assert.True((await auth.CreateUserAsync(new FullWorth.Web.Modules.Auth.CreateAuthUserRequest(Guid.NewGuid(), email, password))).Succeeded);
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        using var login = await client.PostAsJsonAsync("/auth/login", new { email, password });
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.Contains("Finance.Auth=", StringComparison.Ordinal)).Split(';', 2)[0];

        using var request = new HttpRequestMessage(HttpMethod.Get, "/pension");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Matches(@"href=""/styles/tokens\.[a-z0-9]+\.css""", html);
        Assert.Matches(@"src=""/pages/pension/entry\.[a-z0-9]+\.js""", html);
        Assert.DoesNotContain("href=\"/styles/tokens.css\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/fonts/nunito-variable.woff2\"", html, StringComparison.Ordinal);
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
