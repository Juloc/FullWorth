using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Shared;

/// <summary>
/// Production secret hygiene (SECURITY_ARCHITECTURE "Secrets", work item P0.3). Two concerns:
/// (1) source secrets from Docker secret files rather than environment values, via the widely-used
/// <c>NAME_FILE</c> convention; (2) fail closed — refuse to start in Production when a required secret
/// is missing or still holds a development/default placeholder. Both are no-ops outside Production for
/// secret validation, so local dev and tests keep running with blank/dev defaults.
/// </summary>
public static class SecretBootstrap
{
    public enum SecretKind
    {
        Key,
        ConnectionString,
    }

    /// <summary>
    /// For every environment variable named <c>&lt;KEY&gt;_FILE</c> that points at a readable file, inject the
    /// file's trimmed contents as configuration key <c>&lt;KEY&gt;</c> (with <c>__</c> mapped to <c>:</c>), so a
    /// Docker secret mounted at e.g. <c>/run/secrets/ingest_key</c> referenced by
    /// <c>Security__IngestKey_FILE=/run/secrets/ingest_key</c> becomes <c>Security:IngestKey</c>.
    /// The file value wins over any plain environment value for the same key.
    /// </summary>
    public static void AddSecretFiles(IConfigurationBuilder configuration)
    {
        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key?.ToString();
            if (name is null || !name.EndsWith("_FILE", StringComparison.Ordinal)) continue;
            var path = entry.Value?.ToString();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var key = name[..^"_FILE".Length].Replace("__", ":");
            if (key.Length == 0) continue;
            overlay[key] = File.ReadAllText(path).Trim();
        }
        if (overlay.Count > 0) configuration.AddInMemoryCollection(overlay);
    }

    /// <summary>
    /// Secrets this installation owns, and the number of random bytes each gets.
    ///
    /// Deliberately NOT the database password: postgres creates its own before this process starts,
    /// and a second writer would hand the two halves of this stack different passwords.
    /// </summary>
    private static readonly (string Variable, int Bytes)[] OwnSecrets =
    [
        // Exactly 32, because this one is not an opaque string: FieldCipher reads it as an AES-256 key
        // and refuses any other length. Generated with 48 it was never usable, and nothing noticed -
        // a fresh host failed earlier still, on the external secrets volume compose would not create.
        ("Security__DataEncryptionKey_FILE", 32),
        ("Security__InternalKey_FILE", 48),
        ("Security__IngestKey_FILE", 48),
        ("Security__ApiKey_FILE", 48),
        ("AiAccess__CodexBridgeKey_FILE", 48)
    ];

    /// <summary>
    /// Creates the secrets this installation owns, on a host that does not have them yet.
    ///
    /// This used to be FULLWORTH_SECRET: one value an operator had to invent and put in an .env file,
    /// which then stood in for EVERY missing secret - so the same string was at once the database
    /// password, the backend internal key, the ingest key, the banking API key and the Codex bridge
    /// key. Anything that learned it could act as any user through the internal-context middleware.
    /// Replacing it with a script an operator had to remember before the first start was no better:
    /// it turns "docker compose up -d" into "docker compose up -d, but first".
    ///
    /// Separate random values per purpose, created by the process that reads them, at no cost.
    ///
    /// CREATE-ONLY, and for data_encryption_key that is the whole ballgame: overwriting it makes every
    /// encrypted column in the database unreadable, and no Postgres backup brings it back.
    ///
    /// Call BEFORE <see cref="AddSecretFiles"/> - that is what reads the files back in.
    /// </summary>
    /// <returns>
    /// The variable names whose secret this call CREATED. Empty on every later start, because the
    /// secrets already exist. That distinction is load-bearing: a data_encryption_key created right
    /// now, on an installation whose database already holds rows, cannot be the key those rows were
    /// encrypted with - see <c>DataEncryptionKeyGuard</c>.
    /// </returns>
    public static IReadOnlyList<string> EnsureOwnSecrets() =>
        EnsureOwnSecrets(Environment.GetEnvironmentVariables());

    /// <inheritdoc cref="EnsureOwnSecrets()"/>
    public static IReadOnlyList<string> EnsureOwnSecrets(System.Collections.IDictionary environment)
    {
        var created = new List<string>();
        foreach (var (variable, bytes) in OwnSecrets)
        {
            var path = environment[variable]?.ToString();
            if (string.IsNullOrWhiteSpace(path)) continue;

            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0) continue;

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                var secret = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes));

                // Written under a temporary name and moved into place, so a reader never sees half a
                // secret, and overwrite: false so a racing sibling process cannot clobber the winner.
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, secret);
                File.Move(temp, path, overwrite: false);
                created.Add(variable);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A read-only mount, a directory this process may not write to, or a sibling that won
                // the race. None is worth refusing to start over here: the value is simply absent and
                // RequireSecret says so, by name, in Production. Not counted as created either - if a
                // sibling won the race, it created it, not us.
                //
                // But it is worth SAYING. Swallowed silently, a root-owned secrets directory surfaced
                // three layers later as "Services:BackendInternalKey must be configured with a
                // sufficiently long secret" - a message about configuration, for a permission problem,
                // naming a key the operator never configured in the first place.
                if (!File.Exists(path))
                    Console.Error.WriteLine(
                        $"FullWorth could not create {variable} at {path}: {exception.Message}");
            }
        }

        return created;
    }

    /// <summary>
    /// Configuration key set when <see cref="EnsureOwnSecrets()"/> created the data encryption key in
    /// THIS start. Carried as configuration rather than static state so it survives the unified host,
    /// where FullWorth.Web bootstraps the secrets and FullWorth.Backend is the one that has to decide
    /// whether they can possibly belong to the database it is about to open.
    /// </summary>
    public const string DataEncryptionKeyCreatedNowKey = "Security:DataEncryptionKeyCreatedNow";

    /// <summary>Records what <see cref="EnsureOwnSecrets()"/> just created, for later startup checks.</summary>
    public static void NoteCreatedSecrets(
        IConfigurationBuilder configuration,
        IReadOnlyList<string> created) =>
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataEncryptionKeyCreatedNowKey] =
                created.Contains("Security__DataEncryptionKey_FILE") ? "true" : "false"
        });

    /// <summary>
    /// Builds the database connection strings from their parts when they are not given whole.
    ///
    /// A connection string cannot arrive through the <c>&lt;KEY&gt;_FILE</c> convention, because only a
    /// fragment of it - the password - is a secret. That is why the deploy stack used to carry a shell
    /// entrypoint whose whole job was to <c>cat</c> the password file and interpolate it into two
    /// connection strings. It cost more than it looks: the secret then reached the process as an
    /// environment value, where <c>/proc/self/environ</c> and every crash dump can read it, and the
    /// entrypoint lived in a volume rather than in the image.
    ///
    /// So host, port, database and user stay plain configuration under <c>Database:*</c> and only the
    /// password comes from a file, as <c>Database__Password_FILE</c>. An explicitly configured
    /// <c>ConnectionStrings:*</c> still wins, so every existing deployment and every test that passes
    /// one keeps working untouched.
    ///
    /// Call this AFTER <see cref="AddSecretFiles"/>: the password it needs is what that overlays.
    /// </summary>
    public static void AddComposedConnectionStrings(IConfigurationManager configuration)
    {
        var password = configuration["Database:Password"];
        if (string.IsNullOrWhiteSpace(password)) return;

        var host = Value(configuration, "Database:Host", "fullworth-postgres");
        var port = Value(configuration, "Database:Port", "5432");
        var database = Value(configuration, "Database:Name", "fullworth");
        var user = Value(configuration, "Database:User", "fullworth");
        var composed = $"Host={host};Port={port};Database={database};Username={user};Password={password}";

        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // Both names, one database: the finance model and ASP.NET Identity share it, and the deploy
        // stack set the same value twice for exactly that reason.
        foreach (var name in new[] { "ConnectionStrings:FullWorth", "ConnectionStrings:AuthDatabase" })
            if (string.IsNullOrWhiteSpace(configuration[name]))
                overlay[name] = composed;

        if (overlay.Count > 0) configuration.AddInMemoryCollection(overlay);
    }

    private static string Value(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>
    /// In Production, throw if <paramref name="key"/> is missing or looks like a development/default
    /// placeholder. Outside Production this is a no-op so dev/test can run with blank/dev secrets.
    /// The offending key name is reported; the secret value is never included in the message.
    /// </summary>
    public static void RequireSecret(IConfiguration configuration, IHostEnvironment environment, string key, SecretKind kind = SecretKind.Key)
    {
        if (!environment.IsProduction()) return;
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required secret '{key}' is not configured. Provide it via a Docker secret ('{key.Replace(":", "__")}_FILE') or environment value.");
        if (LooksLikePlaceholder(value, kind))
            throw new InvalidOperationException($"Required secret '{key}' still holds a development/default value; set a production secret before exposing the service.");
    }

    internal static bool LooksLikePlaceholder(string value, SecretKind kind)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "default" or "changeme"
            || normalized.Contains("change-me", StringComparison.Ordinal)
            || normalized.Contains("placeholder", StringComparison.Ordinal)
            || normalized.StartsWith("generate-", StringComparison.Ordinal)
            || normalized.StartsWith("replace-", StringComparison.Ordinal))
            return true;

        if (kind == SecretKind.ConnectionString)
        {
            // Reject the exact committed dev connection passwords (whole token, so a real secret like
            // "fullworth_test_password" is not falsely flagged) so a forgotten override can't ship to prod.
            return System.Text.RegularExpressions.Regex.IsMatch(normalized, @"password=(finance|fullworth|postgres)(;|$)");
        }

        // A production API/gate key this short is almost certainly a leftover placeholder.
        return normalized.Length < 16;
    }
}
