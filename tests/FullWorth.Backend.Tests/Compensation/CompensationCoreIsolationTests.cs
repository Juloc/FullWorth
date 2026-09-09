using System.IO;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// FullWorth.Compensation.Core holds the one German payroll formula and its statutory parameters, and
/// it is the assembly the public landing page is meant to run in the browser. That only works while it
/// stays free of server dependencies - a single PackageReference to EF Core or ASP.NET here would end
/// the browser build, and the fallback would be a JavaScript copy of the formula, which is exactly the
/// duplication this feature exists to avoid.
/// </summary>
public sealed class CompensationCoreIsolationTests
{
    [Fact]
    public void TheCalculatorLibraryHasNoDependencies()
    {
        var path = Path.Combine(Root(), "src", "FullWorth.Compensation.Core", "FullWorth.Compensation.Core.csproj");
        Assert.True(File.Exists(path), "FullWorth.Compensation.Core moved; update this guard deliberately.");
        var csproj = File.ReadAllText(path);

        Assert.DoesNotContain("<PackageReference", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.NET.Sdk.Web", csproj, StringComparison.Ordinal);
    }

    // A new project has to reach the release images too. The Dockerfiles copy projects one by one, so a
    // referenced project that is not listed there fails the image build - after CI has gone green.
    [Theory]
    [InlineData("src/FullWorth.Web/Dockerfile")]
    [InlineData("src/FullWorth.Backend/Dockerfile")]
    public void EveryImageThatBuildsTheBackendCopiesTheCalculatorLibrary(string dockerfile)
    {
        var content = File.ReadAllText(Path.Combine(Root(), dockerfile.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("src/FullWorth.Compensation.Core/FullWorth.Compensation.Core.csproj", content, StringComparison.Ordinal);
        Assert.Contains("COPY src/FullWorth.Compensation.Core/ src/FullWorth.Compensation.Core/", content, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
