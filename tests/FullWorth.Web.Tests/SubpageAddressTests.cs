using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// Adressen, die eine Seite selbst in die Adresszeile schreibt, muessen beim Neuladen wieder dieselbe
/// Seite liefern.
///
/// Altersvorsorge und Steuern haben Reiter mit eigener Adresse (/pension/vertraege, /tax/review) und
/// schreiben sie per history.pushState. Solange die alte Huelle jede Adresse mit index.html beantwortete,
/// ging das von selbst. Seit #154 ist jede Adresse eine Razor-Seite und es gibt keinen Rueckfall mehr -
/// und diese Reiter-Adressen hatten keine Seite: ein Klick auf den Reiter ging, das Neuladen danach,
/// ein Lesezeichen oder ein geteilter Link endeten in einer 404.
///
/// Die Adressen werden aus den Seitenmodulen gelesen, nicht hier abgeschrieben - ein neuer Reiter ist
/// damit sofort mitgeprueft.
/// </summary>
public sealed class SubpageAddressTests(FullWorthWebFactory factory) : IClassFixture<FullWorthWebFactory>
{
    private static string[] Addresses() =>
        Regex.Matches(WebSources.Asset("pages", "pension", "page.js"), """path: '(?<path>/pension/[a-z-]+)'""")
            .Select(match => match.Groups["path"].Value)
            .Concat(Regex.Matches(WebSources.Asset("pages", "tax", "page.js"), """'(?<path>/tax/[a-z-]+)'""")
            .Select(match => match.Groups["path"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static TheoryData<string> TabAddresses() => new(Addresses());

    [Fact]
    public void The_tab_addresses_are_found()
    {
        // Findet das Muster nichts, waere die Theorie unten ueber eine leere Liste gruen.
        var addresses = Addresses();
        Assert.Contains("/pension/vertraege", addresses);
        Assert.Contains("/tax/review", addresses);
    }

    [Theory]
    [MemberData(nameof(TabAddresses))]
    public async Task A_tab_address_opens_its_page_again(string path)
    {
        using var response = await GetSignedInAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = path.Split('/')[1];
        Assert.Contains($"data-view=\"{view}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>Die Gegenprobe: nicht jede Adresse unter der Seite ist eine - Unbekanntes bleibt 404.</summary>
    [Theory]
    [InlineData("/pension/gibt-es-nicht")]
    [InlineData("/tax/gibt-es-nicht")]
    public async Task An_unknown_address_below_a_page_is_still_not_found(string path)
    {
        using var response = await GetSignedInAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetSignedInAsync(string path)
    {
        const string password = "correct horse battery staple";
        var email = $"subpage-{Guid.NewGuid():N}@example.com";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<FullWorth.Web.Modules.Auth.AuthService>();
            Assert.True((await auth.CreateUserAsync(new FullWorth.Web.Modules.Auth.CreateAuthUserRequest(Guid.NewGuid(), email, password))).Succeeded);
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        using var login = await client.PostAsJsonAsync("/auth/login", new { email, password });
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.Contains("Finance.Auth=", StringComparison.Ordinal)).Split(';', 2)[0];

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }
}
