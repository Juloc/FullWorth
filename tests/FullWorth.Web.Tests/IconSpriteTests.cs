using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FullWorth.Web.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// Das SVG-Sprite der Navigation (#154): ein Symbol je Eintrag, feste IDs, eine cache-sichere Adresse.
///
/// Die teure Falle eines Sprites ist die stille: ein &lt;use&gt; auf eine ID, die es nicht gibt, zeichnet
/// einfach nichts - kein Fehler, keine Konsolenmeldung, nur ein leerer Platz in der Leiste. Deshalb
/// prueft der erste Test die Richtung, in der das passiert.
/// </summary>
public sealed class IconSpriteTests(FullWorthWebFactory factory) : IClassFixture<FullWorthWebFactory>
{
    private static HashSet<string> Symbols() =>
        Regex.Matches(WebSources.Asset("icons", "sprite.svg"), """<symbol id="(?<id>[\w-]+)" viewBox="0 0 24 24">""")
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_icon_the_navigation_names_is_in_the_sprite()
    {
        var symbols = Symbols();
        var named = NavigationCatalog.Entries.Select(entry => entry.Icon)
            // Die zwei, die die Leisten selbst tragen: der Pfeil einer Gruppe und "Mehr".
            .Concat(Regex.Matches(WebSources.Navigation() + WebSources.BottomNavigation(), """IconSprite\.Href\(ViewContext\.HttpContext, "(?<id>[\w-]+)"\)""")
                .Select(match => match.Groups["id"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("nav-chevron", named);
        Assert.Contains("nav-more", named);
        var missing = named.Where(id => !symbols.Contains(id)).ToArray();
        Assert.True(missing.Length == 0, "the sprite has no symbol for: " + string.Join(", ", missing));
    }

    /// <summary>Die Geometrie steht im Sprite und nur dort - der Katalog nennt die ID.</summary>
    [Fact]
    public void The_catalogue_names_symbols_instead_of_carrying_geometry()
    {
        Assert.All(NavigationCatalog.Entries, entry => Assert.Matches("^nav-[a-z-]+$", entry.Icon));
        Assert.DoesNotContain("<path", WebSources.Asset("app", "menu.js"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Die gerenderte Leiste verweist mit Fingerabdruck aufs Sprite, und das "Mehr"-Menue, das der
    /// Browser baut, findet dieselbe Adresse am &lt;body&gt;.
    /// </summary>
    [Fact]
    public async Task A_rendered_page_points_at_the_fingerprinted_sprite()
    {
        const string password = "correct horse battery staple";
        var email = $"sprite-{Guid.NewGuid():N}@example.com";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<FullWorth.Web.Modules.Auth.AuthService>();
            Assert.True((await auth.CreateUserAsync(new FullWorth.Web.Modules.Auth.CreateAuthUserRequest(Guid.NewGuid(), email, password))).Succeeded);
        }
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        using var login = await client.PostAsJsonAsync("/auth/login", new { email, password });
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.Contains("Finance.Auth=", StringComparison.Ordinal)).Split(';', 2)[0];

        using var request = new HttpRequestMessage(HttpMethod.Get, "/accounts");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var sprite = Regex.Match(html, """data-sprite="(?<url>/icons/sprite\.[a-z0-9]+\.svg)" """.TrimEnd());
        Assert.True(sprite.Success, "the <body> does not carry the fingerprinted sprite address");
        Assert.Contains($"<use href=\"{sprite.Groups["url"].Value}#nav-accounts\">", html, StringComparison.Ordinal);
        Assert.Contains($"<use href=\"{sprite.Groups["url"].Value}#nav-chevron\">", html, StringComparison.Ordinal);
    }
}
