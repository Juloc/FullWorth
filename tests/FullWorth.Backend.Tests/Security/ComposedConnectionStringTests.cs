using FullWorth.Shared;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Security;

/// <summary>
/// The database connection strings are assembled from their parts, so the password can arrive as a file.
///
/// A connection string cannot come through the <c>&lt;KEY&gt;_FILE</c> convention, because only a
/// fragment of it is a secret. That is why the deploy stack carried a shell entrypoint whose entire job
/// was to <c>cat</c> the password file and interpolate it — which put the secret into the process
/// environment, where <c>/proc/self/environ</c> and every crash dump can read it, and put the entrypoint
/// in a volume rather than in the image.
/// </summary>
public sealed class ComposedConnectionStringTests
{
    [Fact]
    public void The_connection_string_is_composed_from_the_password_and_the_plain_parts()
    {
        var configuration = Manager(new()
        {
            ["Database:Host"] = "db.internal",
            ["Database:Name"] = "fullworth_prod",
            ["Database:User"] = "fullworth_app",
            ["Database:Password"] = "from-the-file"
        });

        SecretBootstrap.AddComposedConnectionStrings(configuration);

        Assert.Equal(
            "Host=db.internal;Port=5432;Database=fullworth_prod;Username=fullworth_app;Password=from-the-file",
            configuration.GetConnectionString("FullWorth"));
    }

    /// <summary>
    /// Both names, one database. The finance model and ASP.NET Identity share it — the deploy stack set
    /// the same value twice for exactly that reason.
    /// </summary>
    [Fact]
    public void Both_the_finance_and_the_auth_connection_string_are_filled()
    {
        var configuration = Manager(new() { ["Database:Password"] = "pw" });

        SecretBootstrap.AddComposedConnectionStrings(configuration);

        Assert.Equal(
            configuration.GetConnectionString("FullWorth"),
            configuration.GetConnectionString("AuthDatabase"));
    }

    /// <summary>
    /// An explicit connection string still wins, per key. Every existing deployment and every test that
    /// passes one has to keep working untouched — this is about removing a shell script, not about
    /// forcing a new way to configure a database.
    /// </summary>
    [Fact]
    public void An_explicit_connection_string_is_never_overwritten()
    {
        var configuration = Manager(new()
        {
            ["Database:Password"] = "pw",
            ["ConnectionStrings:FullWorth"] = "Host=explicit;Database=explicit"
        });

        SecretBootstrap.AddComposedConnectionStrings(configuration);

        Assert.Equal("Host=explicit;Database=explicit", configuration.GetConnectionString("FullWorth"));
        // The one that was NOT given explicitly still gets composed.
        Assert.Contains("Password=pw", configuration.GetConnectionString("AuthDatabase"));
    }

    /// <summary>
    /// Without a password there is nothing to compose — and nothing is invented. A half-built connection
    /// string would fail later with an error about the database rather than about the missing secret.
    /// </summary>
    [Fact]
    public void Without_a_password_nothing_is_composed()
    {
        var configuration = Manager(new() { ["Database:Host"] = "db.internal" });

        SecretBootstrap.AddComposedConnectionStrings(configuration);

        Assert.Null(configuration.GetConnectionString("FullWorth"));
        Assert.Null(configuration.GetConnectionString("AuthDatabase"));
    }

    /// <summary>
    /// The password reaches this through <see cref="SecretBootstrap.AddSecretFiles"/>, which is the whole
    /// point: a file on disk, never an environment value.
    /// </summary>
    [Fact]
    public void The_password_can_come_from_a_mounted_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "fullworth-db-pw-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "secret-from-disk\n");
        var variable = "Database__Password_FILE";
        Environment.SetEnvironmentVariable(variable, path);
        try
        {
            var configuration = Manager([]);
            SecretBootstrap.AddSecretFiles(configuration);
            SecretBootstrap.AddComposedConnectionStrings(configuration);

            // Trimmed: a secret written with printf or echo ends in a newline more often than not, and a
            // trailing newline inside a password surfaces as "wrong credentials".
            Assert.Contains("Password=secret-from-disk", configuration.GetConnectionString("FullWorth"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            File.Delete(path);
        }
    }

    private static ConfigurationManager Manager(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        if (values.Count > 0) configuration.AddInMemoryCollection(values);
        return configuration;
    }
}
