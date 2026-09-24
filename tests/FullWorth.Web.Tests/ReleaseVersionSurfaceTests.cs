namespace FullWorth.Web.Tests;

public sealed class ReleaseVersionSurfaceTests
{
    [Fact]
    public void Docker_release_uses_one_version_for_assembly_and_visible_badge()
    {
        var root = FindRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "src", "FullWorth.Web", "Dockerfile"));
        // Das Abzeichen steht seit #154 in der Seitenleisten-Partial, nicht mehr in index.html - und der
        // Dockerfile stempelt es dort VOR dem Publish, weil Razor danach uebersetzt ist.
        var shell = WebSources.Navigation();

        Assert.Contains("ARG FULLWORTH_VERSION=0.0.0-dev", dockerfile, StringComparison.Ordinal);
        Assert.Contains("-p:Version=\"$FULLWORTH_VERSION\"", dockerfile, StringComparison.Ordinal);
        Assert.Contains("version_label=\"v${FULLWORTH_VERSION}\"", dockerfile, StringComparison.Ordinal);
        Assert.Contains("brand-beta", dockerfile, StringComparison.Ordinal);
        Assert.Contains("grep -Fq", dockerfile, StringComparison.Ordinal);
        Assert.Contains("<span class=\"brand-beta\">Alpha</span>", shell, StringComparison.Ordinal);
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
