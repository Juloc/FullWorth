using System.IO;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// The import pages are the only pages whose HTML lives inlined in C# rather than in wwwroot, which is
/// how they came to load just app.css: every design token resolved to nothing (--s4, --line, --muted,
/// --surface, --cta) and the responsive layer never applied, so a phone got unstyled 20px form controls.
///
/// A page that hand-writes its own &lt;head&gt; has to carry the whole layer chain. This pins that, because
/// nothing else can: the UI baseline tests assert the contents of the JS module, which passes happily
/// while the page around it is unstyled.
/// </summary>
public sealed class ImportPageStylesheetGuardTests
{
    // In index.html order. tokens first (everything references them), responsive last before the
    // page's own feature sheet so its mobile rules can win.
    private static readonly string[] RequiredChain =
    [
        "/styles/tokens.css",
        "/styles/reset.css",
        "/appearance.css",
        "/styles/shell.css",
        "/styles/components.css",
        "/app.css",
        "/styles/responsive.css"
    ];

    [Theory]
    [InlineData("ImportCenterPage.cs")]
    [InlineData("FinanzguruImportPage.cs")]
    [InlineData("BrokerPdfImportPage.cs")]
    public void EveryInlinedImportPageLoadsTheFullLayerChain(string fileName)
    {
        var path = Path.Combine(Root(), "src", "FullWorth.Web", "Modules", "Import", fileName);
        Assert.True(File.Exists(path), $"{fileName} moved; update this guard deliberately.");
        var html = File.ReadAllText(path);

        var loaded = Regex.Matches(html, "<link[^>]+rel=\"stylesheet\"[^>]+href=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        foreach (var sheet in RequiredChain)
        {
            Assert.Contains(
                sheet,
                loaded,
                StringComparer.Ordinal);
        }

        // Order matters: tokens before anything that reads them, responsive after app.css.
        Assert.True(
            Array.IndexOf(loaded, "/styles/tokens.css") < Array.IndexOf(loaded, "/app.css"),
            $"{fileName} must load tokens.css before app.css, or the tokens app.css reads are undefined.");
        Assert.True(
            Array.IndexOf(loaded, "/app.css") < Array.IndexOf(loaded, "/styles/responsive.css"),
            $"{fileName} must load responsive.css after app.css, or the mobile rules cannot win.");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
