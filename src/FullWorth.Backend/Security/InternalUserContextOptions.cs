using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Security;

public sealed class InternalUserContextOptions
{
    public const string ConfigurationKey = "Security:InternalKey";
    public const int MinimumKeyLength = 32;

    private InternalUserContextOptions(string internalKey)
    {
        InternalKey = internalKey;
    }

    public string InternalKey { get; }

    public static InternalUserContextOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var key = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(key) || key.Length < MinimumKeyLength)
            throw new InvalidOperationException("Security:InternalKey must be configured with a sufficiently long secret.");

        if (environment.IsProduction() && LooksLikePlaceholder(key))
            throw new InvalidOperationException("Security:InternalKey must be replaced with a production secret.");

        return new InternalUserContextOptions(key);
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
