using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Security.Headers;

public sealed class SecurityHeadersSourceAuditTests : IClassFixture<FullWorthWebFactory>
{
    private readonly HttpClient _client;

    public SecurityHeadersSourceAuditTests(FullWorthWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public void HtmlShells_DoNotContainInlineScriptsStylesOrEventHandlers()
    {
        foreach (var file in PublicFiles("*.html"))
        {
            var content = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(content, @"<script\b(?![^>]*\bsrc\s*=)[^>]*>", RegexOptions.IgnoreCase), $"Inline script in {file}");
            Assert.False(Regex.IsMatch(content, @"\son[a-z]+\s*=", RegexOptions.IgnoreCase), $"Inline event handler in {file}");
            Assert.False(Regex.IsMatch(content, @"javascript\s*:", RegexOptions.IgnoreCase), $"javascript: URL in {file}");
            Assert.False(Regex.IsMatch(content, @"\bstyle\s*=", RegexOptions.IgnoreCase), $"Inline style in {file}");
            Assert.False(Regex.IsMatch(content, @"<style\b", RegexOptions.IgnoreCase), $"Inline style block in {file}");
        }
    }

    [Fact]
    public void JavaScript_DoesNotUseEvalNewFunctionOrJavascriptUrls()
    {
        foreach (var file in PublicFiles("*.js"))
        {
            var content = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(content, @"\beval\s*\(", RegexOptions.IgnoreCase), $"eval() in {file}");
            Assert.False(Regex.IsMatch(content, @"\bnew\s+Function\s*\(", RegexOptions.IgnoreCase), $"new Function() in {file}");
            Assert.False(Regex.IsMatch(content, @"javascript\s*:", RegexOptions.IgnoreCase), $"javascript: URL in {file}");
        }
    }

    [Fact]
    public void NoSourceInlineStylesRemain()
    {
        // No STATIC inline style attribute may remain in shipped HTML/JS — those belong in CSS classes.
        // Attributes whose value is a ${...} template binding are exempt: they push a dynamic value into a
        // CSS custom property or computed dimension (no static-class equivalent), which is exactly what the
        // CSP's `style-src-attr 'unsafe-inline'` directive exists to permit. `const style = ...` variable
        // declarations (createElement('style')) are not attributes and never match this pattern.
        var files = PublicFiles("*.html").Concat(PublicFiles("*.js"));
        var occurrences = files.Sum(file => Regex
            .Matches(File.ReadAllText(file), "style\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)
            .Count(match => !match.Groups[1].Value.Contains("${", StringComparison.Ordinal)));

        Assert.Equal(0, occurrences);
    }

    /// <summary>
    /// Markup entsteht nicht nur in .html-Dateien. Es entsteht in Zeichenketten in JavaScript und in
    /// Rohstrings in C# — und dort hat der Wächter oben nie hingesehen.
    ///
    /// Zwei Stellen haben das ausgenutzt, beide über Monate. <c>features/ux-kit.js</c> hängte
    /// <c>onerror="this.remove()"</c> an das Markenlogo: <c>script-src 'self'</c> deckt auch
    /// <c>script-src-attr</c> ab, der Browser hat den Handler verworfen, das fehlgeschlagene Bild blieb
    /// stehen — und da ein Markenlogo einen eigenen Untergrund mitbringt, deckte der leere Rahmen das
    /// Monogramm darunter zu. <c>ShareReceiptEndpoints.Page()</c> schrieb einen <c>&lt;style&gt;</c>-Block
    /// in das Dokument, den <c>style-src 'self'</c> verwirft: die Seite, auf der ein geteilter Beleg
    /// landet, kam unformatiert.
    ///
    /// Beides bricht leise. Kein Bau schlägt fehl, keine Anfrage schlägt fehl, nur der Browser tut
    /// nichts — die Meldung steht in seiner Konsole, die niemand liest.
    /// </summary>
    [Fact]
    public void Nothing_generates_markup_the_policy_refuses_to_run()
    {
        var sources = PublicFiles("*.js")
            .Concat(PublicFiles("*.html"))
            .Concat(Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot(), "src", "FullWorth.Web"), "*.cs", SearchOption.AllDirectories));

        var offenders = new List<string>();
        foreach (var file in sources)
        {
            // Ohne Kommentare, sonst meldet der Wächter den Satz, mit dem jemand erklärt hat, warum es
            // diese Regel gibt. Genau das ist in diesem Projekt schon einmal passiert.
            var code = Regex.Replace(
                Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
                @"(?m)^\s*//.*$", string.Empty);
            var name = Path.GetRelativePath(RepositoryRoot(), file).Replace('\\', '/');

            if (Regex.IsMatch(code, @"<style\b", RegexOptions.IgnoreCase))
                offenders.Add($"{name}: ein <style>-Block — style-src 'self' verwirft ihn");
            // Das Leerzeichen davor gehört dazu: eine Wortgrenze allein macht aus data-oneoff="label"
            // einen Treffer, weil der Bindestrich eine ist. Ein Attribut beginnt nach Zwischenraum
            // oder Anführungszeichen, nie mitten im Namen.
            if (Regex.IsMatch(code, @"[\s""'](?:on[a-z]+)\s*=\s*\\?""", RegexOptions.IgnoreCase))
                offenders.Add($"{name}: ein Ereignis-Attribut — script-src 'self' führt es nicht aus");
        }

        Assert.True(offenders.Count == 0,
            "Das schickt der Server aus, und der Browser weigert sich:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    [Theory]
    [InlineData("/app/boot.js", "javascript")]
    [InlineData("/app.js", "javascript")]
    [InlineData("/auth/auth.js", "javascript")]
    [InlineData("/styles/app.css", "text/css")]
    [InlineData("/auth/auth.css", "text/css")]
    [InlineData("/locales/de.json", "application/json")]
    public async Task CriticalStaticAssets_HaveNosniffCompatibleContentTypes(string path, string expectedMediaType)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        if (expectedMediaType == "javascript")
            Assert.Contains("javascript", mediaType, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Equal(expectedMediaType, mediaType);
    }

    private static IEnumerable<string> PublicFiles(string pattern) =>
        Directory.EnumerateFiles(WebRoot(), pattern, SearchOption.AllDirectories);

    private static string WebRoot() => Path.Combine(RepositoryRoot(), "src", "FullWorth.Web", "wwwroot");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate FullWorth.slnx from test output directory.");
    }
}
