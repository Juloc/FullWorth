namespace FullWorth.Backend.Tests.Infrastructure;

/// <summary>
/// No version was set anywhere, so every assembly built as 1.0.0.0 and every instance reported
/// "clientVersion 1.0.0.0" to the Cloud — which could therefore not tell one client from another, and
/// the knowledge-pack minimum-client-version check compared against a constant.
///
/// The version now comes from the build. These tests hold the wiring in place: the reader, the props
/// default, and the three places that have to pass the version through or it silently reverts to the
/// dev default in the shipped image.
/// </summary>
public sealed class BuildVersionTests
{
    [Fact]
    public void The_reported_version_is_not_the_missing_version_default()
    {
        Assert.False(string.IsNullOrWhiteSpace(FullWorthVersion.Full));
        Assert.NotEqual("1.0.0.0", FullWorthVersion.Full);
        Assert.NotEqual("unknown", FullWorthVersion.Full);
    }

    // MSBuild appends "+<commit sha>" to the informational version. The sha is build provenance, not a
    // version, and it would make every reported value unique for no benefit.
    [Fact]
    public void The_reported_version_carries_no_commit_suffix()
    {
        Assert.DoesNotContain('+', FullWorthVersion.Full);
    }

    // A prerelease label is not orderable as a Version, so the numeric form is what a comparison uses.
    [Fact]
    public void A_numeric_version_is_available_for_comparison()
    {
        Assert.NotNull(FullWorthVersion.Numeric);
    }

    [Fact]
    public void Directory_build_props_sets_a_version_default()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));

        Assert.Contains("<Version", props);
    }

    // A Dockerfile that neither receives the version nor copies the props file publishes assemblies
    // with no version at all - which is exactly the state this replaced.
    [Theory]
    [InlineData("src/FullWorth.Web/Dockerfile")]
    [InlineData("src/FullWorth.Backend/Dockerfile")]
    [InlineData("src/FullWorth.Banking/Dockerfile")]
    public void Every_dotnet_image_receives_the_version(string dockerfile)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), dockerfile));

        Assert.Contains("ARG FULLWORTH_VERSION", text);
        Assert.Contains("COPY Directory.Build.props", text);
        Assert.Contains("-p:Version=\"$FULLWORTH_VERSION\"", text);
    }

    [Fact]
    public void The_release_workflow_passes_the_tag_into_the_image_build()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github/workflows/release.yml"));

        // Once per architecture: an image built without it would carry the dev default.
        Assert.Equal(2, Occurrences(workflow, "FULLWORTH_VERSION=${{ steps.vars.outputs.version }}"));
        Assert.Equal(2, Occurrences(workflow, "echo \"version=$version\""));
    }

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }
}
