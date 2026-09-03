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

    [Theory]
    [InlineData("/theme-init.js", "javascript")]
    [InlineData("/app.js", "javascript")]
    [InlineData("/auth/auth.js", "javascript")]
    [InlineData("/app.css", "text/css")]
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
