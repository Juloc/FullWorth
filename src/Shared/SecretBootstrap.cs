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
