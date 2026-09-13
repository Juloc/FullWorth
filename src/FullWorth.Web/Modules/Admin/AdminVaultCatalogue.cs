using Microsoft.Extensions.Configuration;

namespace FullWorth.Web.Modules.Admin;

/// <summary>Where a vault entry's value comes from, which decides how it is read.</summary>
public enum VaultSource
{
    /// <summary>A configuration key of this host — a secret file, an environment variable, appsettings.</summary>
    Configuration,

    /// <summary>The data-protected sign-in provider settings, decrypted on demand.</summary>
    ExternalAuth,

    /// <summary>A FieldCipher column in the finance database, fetched over the internal API.</summary>
    Backend
}

/// <summary>
/// One thing the vault can show. The reference is what travels over the wire — never a value, and
/// never a column name the browser could turn into a query.
/// </summary>
/// <param name="Reference">Stable id used by <c>POST /auth/admin/vault/reveal</c>.</param>
/// <param name="Group">Grouping label for the UI.</param>
/// <param name="Label">Human name. Lives here, not in the frontend: FrontendBaselineTests forbids
/// secret-key literals in <c>wwwroot/admin/*.js</c>, and a table of secret names in a public file is
/// a map of what to steal.</param>
/// <param name="RequiresFreshFactor">The factor has to be proven again for THIS reveal, not once for
/// ten. Reserved for the secrets that own the whole installation.</param>
/// <param name="ConfigurationKeys">The configuration keys this value can arrive under, most specific
/// first. There is more than one because the same secret FILE is read under two names: the module
/// that owns it calls it <c>Security:InternalKey</c>, and the BFF that talks to that module calls it
/// <c>Services:BackendInternalKey</c>. Which of them is populated depends on how the host was
/// composed, so the vault looks for all of them rather than assuming a shape.</param>
public sealed record VaultEntryDescriptor(
    string Reference,
    VaultSource Source,
    string Group,
    string Label,
    string Description,
    bool RequiresFreshFactor = false,
    IReadOnlyList<string>? ConfigurationKeys = null);

/// <summary>
/// Everything an administrator may look at, and the four that are worth more than all the rest put
/// together.
///
/// What is deliberately absent: other users' TOTP keys, account passwords and PINs (PBKDF2 — nobody
/// can show those, and no setting changes that), recovery codes, and the pension policy number. The
/// last one is not a credential at all; it is a financial fact, and the admin surfaces do not show
/// financial data.
/// </summary>
public static class AdminVaultCatalogue
{
    public const string InfrastructureGroup = "Infrastruktur";
    public const string SignInGroup = "Anmeldeanbieter";

    /// <summary>
    /// The four that turn "an admin session was stolen" into "the installation is gone": this key
    /// decrypts every encrypted column for every user forever, this one acts as any user through the
    /// internal-context middleware, this one is the ingest path, and this one is the database itself -
    /// including the audit table that is supposed to record the theft.
    /// </summary>
    public static readonly IReadOnlyList<VaultEntryDescriptor> All =
    [
        new("infra.data-encryption-key", VaultSource.Configuration, InfrastructureGroup,
            "Datenschlüssel",
            "Entschlüsselt jede verschlüsselte Spalte dieser Installation. Geht er verloren, hilft kein Datenbank-Backup.",
            RequiresFreshFactor: true, ConfigurationKeys: ["Security:DataEncryptionKey"]),

        new("infra.internal-key", VaultSource.Configuration, InfrastructureGroup,
            "Interner Schlüssel",
            "Wer ihn hat, kann im Namen jedes Nutzers handeln.",
            RequiresFreshFactor: true,
            ConfigurationKeys: ["Security:InternalKey", "Services:BackendInternalKey"]),

        new("infra.ingest-key", VaultSource.Configuration, InfrastructureGroup,
            "Ingest-Schlüssel",
            "Zugang zur internen Aufnahme-API.",
            RequiresFreshFactor: true, ConfigurationKeys: ["Security:IngestKey", "Backend:IngestKey"]),

        new("infra.db-password", VaultSource.Configuration, InfrastructureGroup,
            "Datenbank-Passwort",
            "Vollzugriff auf die Datenbank, einschließlich des Protokolls, das diesen Zugriff aufzeichnet.",
            RequiresFreshFactor: true, ConfigurationKeys: ["Database:Password"]),

        new("infra.banking-key", VaultSource.Configuration, InfrastructureGroup,
            "Banking-API-Schlüssel",
            "Der Schlüssel zwischen Anwendung und Banking-Modul.",
            ConfigurationKeys: ["Security:ApiKey", "Services:BankingApiKey"]),

        new("infra.codex-bridge-key", VaultSource.Configuration, InfrastructureGroup,
            "Codex-Bridge-Schlüssel",
            "Der einzige Schlüssel, den der Codex-Prozess in diesem Container lesen darf.",
            ConfigurationKeys: ["AiAccess:CodexBridgeKey"]),

        new("signin.google-client-secret", VaultSource.ExternalAuth, SignInGroup,
            "Google Client Secret",
            "Aus der Google Cloud Console, verschlüsselt gespeichert."),

        new("signin.apple-private-key", VaultSource.ExternalAuth, SignInGroup,
            "Apple Private Key",
            "Der .p8-Schlüssel aus dem Apple Developer Portal, verschlüsselt gespeichert.")
    ];

    /// <summary>The first of an entry's keys that this host actually has a value for.</summary>
    public static string? ResolveValue(VaultEntryDescriptor descriptor, IConfiguration configuration) =>
        (descriptor.ConfigurationKeys ?? [])
            .Select(key => configuration[key])
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public static VaultEntryDescriptor? Find(string? reference) =>
        string.IsNullOrWhiteSpace(reference)
            ? null
            : All.FirstOrDefault(entry => string.Equals(entry.Reference, reference, StringComparison.Ordinal));
}
