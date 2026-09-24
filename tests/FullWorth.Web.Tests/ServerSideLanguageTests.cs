using FullWorth.Web.Navigation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace FullWorth.Web.Tests;

/// <summary>
/// Der Server waehlt die Sprache des ersten Bildes - und muss dabei zum selben Ergebnis kommen wie
/// <c>core/state.js</c> im Browser (#154).
///
/// Warum das ein eigener Test ist: weichen die beiden ab, sieht man keinen Fehler. Man sieht eine
/// Seite, die kurz auf Deutsch erscheint und dann auf Englisch umspringt - jede Beschriftung
/// aendert ihre Breite, und alles daneben rutscht. Gemessen hat das die Kopfzeile von <c>/rules</c>
/// 116 px gekostet ("Erneut anwenden / Hinzufuegen" -> "Re-apply / Add").
///
/// Die Falle steckt im Cookie: es traegt die gespeicherte Wahl, wird aber von <c>app/boot.js</c>
/// gesetzt, also im Browser. Beim ersten Aufruf gibt es noch keines. Wer nur das Cookie liest,
/// liefert dem englischen Besucher genau einmal deutsches Markup - und "nur einmal" ist die
/// Ausnahme, die die Regel nicht kennt.
/// </summary>
public class ServerSideLanguageTests
{
    private static HttpContext Request(string? acceptLanguage = null, string? cookie = null)
    {
        var context = new DefaultHttpContext();
        if (acceptLanguage is not null) context.Request.Headers.AcceptLanguage = acceptLanguage;
        if (cookie is not null) context.Request.Headers.Cookie = $"{LocaleText.CookieName}={cookie}";
        return context;
    }

    [Theory]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("en", "en")]
    [InlineData("de-DE,de;q=0.9,en;q=0.8", "de")]
    [InlineData("de", "de")]
    [InlineData("fr-FR,fr;q=0.9", "en")]
    public void The_first_visit_follows_the_browser_because_there_is_no_cookie_yet(
        string acceptLanguage, string expected)
    {
        Assert.Equal(expected, LocaleText.Language(Request(acceptLanguage)));
    }

    [Fact]
    public void Without_any_hint_it_stays_german()
    {
        Assert.Equal("de", LocaleText.Language(Request()));
        Assert.Equal("de", LocaleText.Language(Request(acceptLanguage: string.Empty)));
        Assert.Equal("de", LocaleText.Language(null));
    }

    [Theory]
    [InlineData("en", "de-DE,de;q=0.9", "en")]
    [InlineData("de", "en-US,en;q=0.9", "de")]
    public void A_stored_choice_beats_the_browser(string cookie, string acceptLanguage, string expected)
    {
        Assert.Equal(expected, LocaleText.Language(Request(acceptLanguage, cookie)));
    }

    [Fact]
    public void A_cookie_that_says_nothing_useful_does_not_override_the_browser()
    {
        // Sonst faengt ein verirrter Wert die englische Sitzung ab und der Sprung ist zurueck.
        Assert.Equal("en", LocaleText.Language(Request("en-US,en;q=0.9", cookie: "fr")));
        Assert.Equal("en", LocaleText.Language(Request("en-US,en;q=0.9", cookie: string.Empty)));
    }

    [Fact]
    public void The_two_languages_really_carry_different_words()
    {
        // Ohne diese Gegenprobe waere alles darueber wertlos: laegen beide Sprachdateien gleich,
        // koennte die Wahl beliebig falsch sein, ohne dass je ein Wort anders aussaehe.
        var text = new LocaleText(new WwwRootEnvironment());
        Assert.NotEqual(text.Get("common.add", "de"), text.Get("common.add", "en"));
        Assert.False(string.IsNullOrWhiteSpace(text.Get("common.add", "en")));
    }

    /// <summary>Nur der eine Pfad, den <see cref="LocaleText"/> braucht - kein ganzer Host dafuer.</summary>
    private sealed class WwwRootEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "FullWorth.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Root();
        public string EnvironmentName { get; set; } = "Test";

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
