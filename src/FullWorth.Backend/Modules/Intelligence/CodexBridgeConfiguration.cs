namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// The one place that reads the Codex sidecar's configuration.
///
/// Two namespaces had grown side by side and different consumers read different ones: the AI provider
/// resolvers read <c>AiAccess:CodexBridge*</c> and fell back to <c>CodexTest:*</c>, while the receipt
/// bridge and the payslip extractor read <c>CodexTest:*</c> only. So an operator who configured just
/// one namespace got half the features — and the deploy stack had to export the same secret twice
/// under two names to work at all.
///
/// <c>AiAccess:CodexBridge*</c> is the canonical namespace; <c>CodexTest:*</c> is still accepted so an
/// existing deployment keeps running, and <see cref="LegacyKeysInUse"/> lets the host say so once at
/// startup instead of leaving the operator to discover it.
/// </summary>
internal static class CodexBridgeConfiguration
{
    /// <summary>The sidecar's address inside the compose network. Not a production domain.</summary>
    internal const string DefaultBaseUrl = "http://fullworth-codex:8080";

    private const string CanonicalEnabled = "AiAccess:CodexBridgeEnabled";
    private const string CanonicalBaseUrl = "AiAccess:CodexBridgeBaseUrl";
    private const string CanonicalKey = "AiAccess:CodexBridgeKey";
    private const string LegacyEnabled = "CodexTest:Enabled";
    private const string LegacyBaseUrl = "CodexTest:BaseUrl";
    private const string LegacyKey = "CodexTest:BridgeKey";

    /// <summary>
    /// Whether the Codex features that gate on it may run at all. Defaults to false, which is what the
    /// only namespace that ever had this flag defaulted to.
    /// </summary>
    internal static bool IsEnabled(IConfiguration configuration) =>
        Bool(configuration, CanonicalEnabled) ?? Bool(configuration, LegacyEnabled) ?? false;

    internal static string BaseUrl(IConfiguration configuration) =>
        (First(configuration, CanonicalBaseUrl, LegacyBaseUrl) ?? DefaultBaseUrl).TrimEnd('/');

    /// <summary>The internal key. Null when none is configured — never a placeholder.</summary>
    internal static string? Key(IConfiguration configuration) =>
        First(configuration, CanonicalKey, LegacyKey);

    /// <summary>
    /// Resolves the base URL and validates it in one step, because every caller needs both and each of
    /// them used to repeat the check. Returns null when the value is not a usable http URL.
    /// </summary>
    internal static Uri? BaseUri(IConfiguration configuration) =>
        Uri.TryCreate(BaseUrl(configuration), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
            ? uri
            : null;

    /// <summary>
    /// The legacy keys that are set while their canonical counterpart is not — for one startup warning.
    /// Only key NAMES are reported, never a value: the bridge key is a secret.
    /// </summary>
    internal static IReadOnlyList<string> LegacyKeysInUse(IConfiguration configuration) =>
    [
        .. new[]
        {
            (Legacy: LegacyEnabled, Canonical: CanonicalEnabled),
            (Legacy: LegacyBaseUrl, Canonical: CanonicalBaseUrl),
            (Legacy: LegacyKey, Canonical: CanonicalKey)
        }
        .Where(pair => !string.IsNullOrWhiteSpace(configuration[pair.Legacy])
                       && string.IsNullOrWhiteSpace(configuration[pair.Canonical]))
        .Select(pair => $"{pair.Legacy} (use {pair.Canonical})")
    ];

    private static string? First(IConfiguration configuration, string canonical, string legacy)
    {
        var value = configuration[canonical];
        if (!string.IsNullOrWhiteSpace(value)) return value;
        value = configuration[legacy];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? Bool(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var value) ? value : null;
}
