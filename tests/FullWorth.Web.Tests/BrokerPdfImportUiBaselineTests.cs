namespace FullWorth.Web.Tests;

public sealed class BrokerPdfImportUiBaselineTests
{
    [Fact]
    public void New_broker_pdf_portfolio_is_created_by_the_import_commit()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "FullWorth.Web", "wwwroot", "pages", "settings", "import", "broker-pdf", "page.js"));

        Assert.Contains("function portfolioTarget()", source, StringComparison.Ordinal);
        Assert.Contains("portfolioId:value,createPortfolio:null", source, StringComparison.Ordinal);
        Assert.Contains("portfolioId:null,createPortfolio:{", source, StringComparison.Ordinal);
        Assert.Contains("JSON.stringify({...target,securityMappings:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("async function ensurePortfolio", source, StringComparison.Ordinal);
        Assert.DoesNotContain("const created=await api(`api/investments/portfolios?", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the FullWorth repository root.");
    }
}
