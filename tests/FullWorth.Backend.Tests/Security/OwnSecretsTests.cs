using FullWorth.Shared;

namespace FullWorth.Backend.Tests.Security;

/// <summary>
/// A host that has never run FullWorth creates the secrets it owns, by itself.
///
/// This used to be <c>FULLWORTH_SECRET</c>: one value an operator had to invent and put in an .env file,
/// which then stood in for EVERY missing secret — so the same string was at once the database password,
/// the backend internal key, the ingest key, the banking API key and the Codex bridge key. Anything that
/// learned it could act as any user through the internal-context middleware. Replacing it with a script
/// an operator had to remember before the first start was no better: it turns "docker compose up -d"
/// into "docker compose up -d, but first".
/// </summary>
public sealed class OwnSecretsTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "fullworth-own-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Every_secret_this_installation_owns_is_created_and_all_of_them_differ()
    {
        var environment = Environment(
            "Security__DataEncryptionKey_FILE", "Security__InternalKey_FILE", "Security__IngestKey_FILE",
            "Security__ApiKey_FILE", "AiAccess__CodexBridgeKey_FILE");

        SecretBootstrap.EnsureOwnSecrets(environment);

        var values = environment.Values.Cast<string>().Select(File.ReadAllText).ToArray();
        Assert.All(values, value => Assert.True(value.Length >= 32, "A production key this short would be refused."));

        // Separate values per purpose is the entire point: one string for all of them is what
        // FULLWORTH_SECRET was.
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    /// <summary>
    /// Create-only — and for the data encryption key that is the whole ballgame. Overwriting it makes
    /// every encrypted column in the database unreadable, and no Postgres backup brings it back.
    /// </summary>
    [Fact]
    public void An_existing_secret_is_never_rewritten()
    {
        var environment = Environment("Security__DataEncryptionKey_FILE");
        var path = (string)environment["Security__DataEncryptionKey_FILE"]!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "the-key-that-decrypts-everything");

        SecretBootstrap.EnsureOwnSecrets(environment);
        SecretBootstrap.EnsureOwnSecrets(environment);

        Assert.Equal("the-key-that-decrypts-everything", File.ReadAllText(path));
    }

    /// <summary>
    /// The database password is NOT one of them. Postgres creates its own before this process starts,
    /// and a second writer would hand the two halves of this stack different passwords.
    /// </summary>
    [Fact]
    public void The_database_password_is_not_this_process_to_create()
    {
        var environment = Environment("Database__Password_FILE");

        SecretBootstrap.EnsureOwnSecrets(environment);

        Assert.False(File.Exists((string)environment["Database__Password_FILE"]!));
    }

    /// <summary>A variable that points nowhere is not a reason to invent a file somewhere.</summary>
    [Fact]
    public void A_variable_that_is_not_set_creates_nothing()
    {
        SecretBootstrap.EnsureOwnSecrets(new Dictionary<string, string>());

        Assert.False(Directory.Exists(directory));
    }

    private Dictionary<string, string> Environment(params string[] variables) =>
        variables.ToDictionary(
            name => name,
            name => Path.Combine(directory, name.ToLowerInvariant()));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
