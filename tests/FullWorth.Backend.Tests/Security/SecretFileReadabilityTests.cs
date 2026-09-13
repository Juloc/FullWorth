using FullWorth.Shared;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Security;

/// <summary>
/// What happens when a <c>&lt;NAME&gt;_FILE</c> variable points at a file this process may not read.
///
/// It used to be an unhandled exception during host construction — a stack trace from the third line
/// of Program.cs, naming neither the variable nor the reason. That was survivable while one user
/// lived in the container. It stopped being survivable the moment Codex got its own uid: the loop
/// sees EVERY such variable in the environment, including ones that belong to a different user, so
/// "unreadable" became a normal state rather than a broken installation.
///
/// The container this was found in restart-looped with the trace above, which is the loudest possible
/// way to say nothing useful. Now it says which variable, at which path, and why, and carries on —
/// <c>RequireSecret</c> is what decides whether the missing value is actually fatal.
/// </summary>
public sealed class SecretFileReadabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "fullworth-secretfile-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _variables = [];

    public SecretFileReadabilityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var variable in _variables) Environment.SetEnvironmentVariable(variable, null);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private void Set(string variable, string? value)
    {
        _variables.Add(variable);
        Environment.SetEnvironmentVariable(variable, value);
    }

    [Fact]
    public void A_readable_secret_file_becomes_configuration()
    {
        var path = Path.Combine(_directory, "readable");
        File.WriteAllText(path, "  the-secret\n");
        Set("Test__ReadableSecret_FILE", path);

        var builder = new ConfigurationBuilder();
        SecretBootstrap.AddSecretFiles(builder);

        // Trimmed: a secret written with echo ends in a newline more often than not, and a trailing
        // newline inside a key surfaces as "wrong credentials".
        Assert.Equal("the-secret", builder.Build()["Test:ReadableSecret"]);
    }

    /// <summary>
    /// The regression. An unreadable file must not take the whole host down — in the container that
    /// found this, the application restart-looped on a file belonging to the Codex user.
    /// </summary>
    [Fact]
    public void An_unreadable_secret_file_does_not_stop_the_host()
    {
        var unreadable = Path.Combine(_directory, "unreadable");
        File.WriteAllText(unreadable, "not-for-this-process");

        var readable = Path.Combine(_directory, "readable");
        File.WriteAllText(readable, "still-works");

        if (OperatingSystem.IsWindows())
        {
            // No usable mode bits here, so the same class of failure is provoked by pointing at a
            // directory: File.ReadAllText throws UnauthorizedAccessException either way, which is the
            // exception this test is about.
            unreadable = _directory;
        }
        else
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.None);
        }

        Set("Test__Unreadable_FILE", unreadable);
        Set("Test__Readable_FILE", readable);

        var builder = new ConfigurationBuilder();
        var exception = Record.Exception(() => SecretBootstrap.AddSecretFiles(builder));

        Assert.Null(exception);

        var configuration = builder.Build();
        Assert.Null(configuration["Test:Unreadable"]);
        // And the ones it CAN read still arrive. Bailing out of the loop would have been the other
        // easy mistake: one unreadable file would silently unconfigure everything after it.
        Assert.Equal("still-works", configuration["Test:Readable"]);
    }

    [Fact]
    public void A_variable_pointing_nowhere_is_simply_skipped()
    {
        Set("Test__Missing_FILE", Path.Combine(_directory, "does-not-exist"));

        var builder = new ConfigurationBuilder();
        SecretBootstrap.AddSecretFiles(builder);

        Assert.Null(builder.Build()["Test:Missing"]);
    }
}
