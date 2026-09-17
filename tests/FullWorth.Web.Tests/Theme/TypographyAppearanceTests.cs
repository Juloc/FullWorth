using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Theme;

/// <summary>
/// Wächter für #148/#166 ("Use one global Nunito typography system"): genau eine Schriftfamilie, in
/// <c>tokens.css</c> definiert, ohne Umschalter. Der Umschalter (Font-, Größen-, Gewichts- und
/// Zeilenhöhen-Wahl über <c>data-font</c> und <c>finance.typography.*</c>) wurde absichtlich entfernt,
/// nicht vergessen — diese Klasse hielt vorher genau das Gegenteil fest (die alten Werte, die alten
/// Selektoren) und wurde deshalb bei jedem Lauf rot. Sie prüft jetzt die Invariante, die die
/// Entfernung tatsächlich hergestellt hat, damit ein wiedereingeführter Umschalter sie wieder rot
/// macht.
///
/// Der Grund, warum dieses Bündel überhaupt angefasst wurde: die Schrift kam bis eben von einem CDN
/// (<c>cdn.jsdelivr.net</c>), obwohl <c>font-src 'self'</c> (SecurityHeadersPolicy) jede fremde
/// Quelle blockiert — niemand bekam Nunito je zu sehen, jede Seite loggte eine CSP-Verletzung, und
/// unter dem CSP-losen UI-Harness lud die Schrift zusätzlich mit <c>font-display:swap</c> nach dem
/// ersten Zeichnen nach, was LayoutStabilityTests aufdeckte. Jetzt ist die Schrift selbst gehostet und
/// lädt mit <c>font-display:optional</c>, das nie nachtauscht.
/// </summary>
public sealed class TypographyAppearanceTests
{
    /// <summary>
    /// Genau eine Schriftfamilie, definiert in tokens.css, kein Umschalter mehr.
    ///
    /// <c>data-font</c> war der Haken, an dem der alte Umschalter (Fredoka/System/Comic/Mono) hing —
    /// wenn dieser Selektor irgendwo unter wwwroot wieder auftaucht, ist der Umschalter zurück, und
    /// genau das soll dieser Test verhindern.
    /// </summary>
    [Fact]
    public void TokensCssDefinesOneFontFamilyWithNoSwitcher()
    {
        var tokens = File.ReadAllText(WebRootFile("styles", "tokens.css"));

        Assert.Contains("--font-family-base:'Nunito'", tokens);
        Assert.Contains("--font-size-base:16px", tokens);
        Assert.Contains("--letter-spacing-base:0", tokens);

        // Genau ein @font-face in der gesamten Anwendung, und der steht in tokens.css - keine zweite,
        // konkurrierende Definition irgendwo unter wwwroot.
        var fontFaceCount = AllWebRootFiles("*.css")
            .Sum(path => Regex.Matches(File.ReadAllText(path), "@font-face").Count);
        Assert.Equal(1, fontFaceCount);
        Assert.Contains("@font-face", tokens);

        // Der Regressions-Wächter: kein data-font-Selektor irgendwo unter wwwroot. Taucht er wieder
        // auf, ist der entfernte Font-Umschalter zurück.
        foreach (var path in AllWebRootFiles("*"))
        {
            if (IsBinaryAsset(path)) continue;
            Assert.DoesNotContain("data-font", File.ReadAllText(path));
        }
    }

    /// <summary>
    /// Die Schrift kommt von dieser Origin, nie von einer dritten URL - das ist der eigentliche Grund
    /// für diese Änderung. Ein <c>@font-face</c> mit <c>http://</c> oder <c>https://</c> im
    /// <c>src</c> würde von <c>font-src 'self'</c> ohnehin blockiert, aber dann unbemerkt: die Schrift
    /// fällt lautlos auf die System-Schrift zurück, nur eine CSP-Meldung in der Konsole verrät es.
    /// Dieser Test verhindert, dass der CDN-Link zurückkommt.
    /// </summary>
    [Fact]
    public void FontFaceIsSelfHostedAndNeverSwaps()
    {
        var tokens = File.ReadAllText(WebRootFile("styles", "tokens.css"));
        var fontFace = Between(tokens, "@font-face{", "}");

        Assert.DoesNotContain("http://", fontFace);
        Assert.DoesNotContain("https://", fontFace);
        Assert.Contains("url(\"/fonts/", fontFace);

        // optional statt swap: der Browser zeichnet entweder sofort mit der Schrift oder für dieses
        // Laden gar nicht mit ihr - nie dazwischen, also kann es keinen Nachtausch-Sprung geben.
        Assert.Contains("font-display:optional", fontFace);

        var fontFile = WebRootFile("fonts", "nunito-variable.woff2");
        Assert.True(File.Exists(fontFile), $"Schriftdatei fehlt: {fontFile}");
    }

    /// <summary>
    /// <c>app/appearance.js</c> darf die alten <c>finance.typography.*</c>-Schlüssel nur noch löschen,
    /// nie mehr schreiben. Sie stehen in <c>LEGACY_TYPOGRAPHY_KEYS</c> ausschließlich, damit ein Profil
    /// aus der Zeit des Umschalters beim nächsten Start aufgeräumt wird - ein erneutes Schreiben wäre
    /// der Umschalter in neuem Gewand.
    /// </summary>
    [Fact]
    public void AppearanceModuleOnlyErasesLegacyTypographyKeys()
    {
        var script = File.ReadAllText(WebRootFile("app", "appearance.js"));

        Assert.Contains("LEGACY_TYPOGRAPHY_KEYS", script);
        Assert.Contains("finance.typography.baseSize", script);
        Assert.Contains("finance.typography.weight", script);
        Assert.Contains("finance.typography.letterSpacing", script);
        Assert.Contains("finance.typography.lineHeight", script);
        Assert.Contains("localStorage.removeItem(key)", script);

        // Der Regressions-Wächter: der Umschalter schrieb seine Auswahl über dataset.typographyInput.
        // Kommt das zurück, ist die UI zurück, nicht nur der Aufräum-Code.
        Assert.DoesNotContain("typographyInput", script);
        Assert.DoesNotContain("setItem('finance.typography", script);
        Assert.DoesNotContain("setItem(\"finance.typography", script);
    }

    /// <summary>
    /// Typografie kommt ausschließlich aus dem Stylesheet, nicht aus JavaScript beim Start.
    /// <c>applyStoredTypography()</c> gab es vor der Entfernung des Umschalters nie in dieser Form in
    /// boot.js - der alte Test verlangte eine Funktion, die nach der Entfernung erst recht nicht mehr
    /// existieren darf. Dieser Test hält die Abwesenheit fest, nicht die Anwesenheit.
    /// </summary>
    [Fact]
    public void BootRestoresChromeButNeverTypography()
    {
        var script = File.ReadAllText(WebRootFile("app", "boot.js"));

        Assert.DoesNotContain("applyStoredTypography", script);
        Assert.DoesNotContain("finance.typography", script);
        Assert.DoesNotContain("finance.font", script);

        // boot.js sagt selbst, warum: Theme, Farben, Seitenleistenbreite und zugeklappte Gruppen
        // laufen hier, weil ein Sprung sonst erst nach dem ersten Bild entstünde - Typografie braucht
        // das nicht, weil sie nie einen Sprung verursachen kann, wenn sie nur aus dem Stylesheet kommt.
        Assert.Contains("Typografie kommt ausschließlich aus tokens.css/reset.css", script);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"marker not found: {start}");
        from += start.Length;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"end marker not found: {end}");
        return text[from..to];
    }

    private static bool IsBinaryAsset(string path) =>
        path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> AllWebRootFiles(string pattern) =>
        Directory.EnumerateFiles(WebRoot(), pattern, SearchOption.AllDirectories);

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "FullWorth.Web", "wwwroot");
    }

    private static string WebRootFile(params string[] parts) =>
        Path.Combine(new[] { WebRoot() }.Concat(parts).ToArray());
}
