using Microsoft.Extensions.Hosting;

namespace FullWorth.Web.Security.BackendContext;

public sealed class BackendContextOptions
{
    public const string ConfigurationKey = "Services:BackendInternalKey";
    public const string BackendUrlKey = "Services:BackendUrl";
    public const int MinimumKeyLength = 32;

    private BackendContextOptions(string internalKey, Uri backendBaseAddress)
    {
        InternalKey = internalKey;
        BackendBaseAddress = backendBaseAddress;
    }

    public string InternalKey { get; }

    /// <summary>
    /// The one and only origin the internal key may ever be sent to. The outbound handler compares
    /// every request target against this before attaching the key (SSRF second gate).
    /// </summary>
    public Uri BackendBaseAddress { get; }

    public static BackendContextOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var key = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(key) || key.Length < MinimumKeyLength)
            throw new InvalidOperationException("Services:BackendInternalKey must be configured with a sufficiently long secret.");

        if (environment.IsProduction() && LooksLikePlaceholder(key))
            throw new InvalidOperationException("Services:BackendInternalKey must be replaced with a production secret.");

        var backendUrl = configuration[BackendUrlKey] ?? "http://fullworth-backend:8080";
        if (!Uri.TryCreate(backendUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
            throw new InvalidOperationException("Services:BackendUrl must be a valid absolute URL.");

        return new BackendContextOptions(key, baseAddress);
    }

    private static bool LooksLikePlaceholder(string key)
    {
        var normalized = key.Trim().ToLowerInvariant();
        return normalized == "default" ||
               normalized.Contains("change-me", StringComparison.Ordinal) ||
               normalized.Contains("placeholder", StringComparison.Ordinal) ||
               normalized.StartsWith("generate-", StringComparison.Ordinal) ||
               normalized.StartsWith("replace-", StringComparison.Ordinal);
    }
}
