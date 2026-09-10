using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Two configuration namespaces had grown side by side and different consumers read different ones: the
/// AI provider resolvers read <c>AiAccess:CodexBridge*</c> with a fallback to <c>CodexTest:*</c>, while
/// the receipt bridge and the payslip extractor read <c>CodexTest:*</c> only. An operator who configured
/// one namespace therefore got half the features and no message saying why — and the deploy stack had to
/// export the same secret twice under two names to work at all.
/// </summary>
public sealed class CodexBridgeConfigurationTests
{
    [Fact]
    public void The_canonical_namespace_configures_everything()
    {
        var configuration = Build(new()
        {
            ["AiAccess:CodexBridgeEnabled"] = "true",
            ["AiAccess:CodexBridgeBaseUrl"] = "http://codex.internal:9000/",
            ["AiAccess:CodexBridgeKey"] = "canonical-key"
        });

        Assert.True(CodexBridgeConfiguration.IsEnabled(configuration));
        Assert.Equal("http://codex.internal:9000", CodexBridgeConfiguration.BaseUrl(configuration));
        Assert.Equal("canonical-key", CodexBridgeConfiguration.Key(configuration));
        Assert.Empty(CodexBridgeConfiguration.LegacyKeysInUse(configuration));
    }

    // This is the case that used to work only for the receipt/payslip half.
    [Fact]
    public void The_legacy_namespace_still_configures_everything()
    {
        var configuration = Build(new()
        {
            ["CodexTest:Enabled"] = "true",
            ["CodexTest:BaseUrl"] = "http://codex.internal:9000",
            ["CodexTest:BridgeKey"] = "legacy-key"
        });

        Assert.True(CodexBridgeConfiguration.IsEnabled(configuration));
        Assert.Equal("http://codex.internal:9000", CodexBridgeConfiguration.BaseUrl(configuration));
        Assert.Equal("legacy-key", CodexBridgeConfiguration.Key(configuration));
    }

    [Fact]
    public void The_canonical_namespace_wins_over_the_legacy_one()
    {
        var configuration = Build(new()
        {
            ["AiAccess:CodexBridgeKey"] = "canonical-key",
            ["CodexTest:BridgeKey"] = "legacy-key"
        });

        Assert.Equal("canonical-key", CodexBridgeConfiguration.Key(configuration));
        // Nothing to warn about: the canonical key is the one in use.
        Assert.Empty(CodexBridgeConfiguration.LegacyKeysInUse(configuration));
    }

    // Only key names may be reported. The bridge key is a secret and must never reach a log line.
    [Fact]
    public void A_legacy_key_in_use_is_named_without_its_value()
    {
        var configuration = Build(new() { ["CodexTest:BridgeKey"] = "super-secret" });

        var reported = CodexBridgeConfiguration.LegacyKeysInUse(configuration);

        Assert.Contains("CodexTest:BridgeKey (use AiAccess:CodexBridgeKey)", reported);
        Assert.DoesNotContain(reported, entry => entry.Contains("super-secret"));
    }

    // Default-off is what the only namespace that ever had this flag defaulted to; turning Codex on by
    // accident would send uploads to a sidecar the operator never configured.
    [Fact]
    public void Codex_is_off_when_nothing_is_configured()
    {
        var configuration = Build([]);

        Assert.False(CodexBridgeConfiguration.IsEnabled(configuration));
        Assert.Null(CodexBridgeConfiguration.Key(configuration));
        Assert.Equal(CodexBridgeConfiguration.DefaultBaseUrl, CodexBridgeConfiguration.BaseUrl(configuration));
    }

    // The sidecar is reached over the compose network. An https or otherwise unusable URL is refused
    // rather than silently retried against a wrong host.
    [Theory]
    [InlineData("https://codex.example.com")]
    [InlineData("not-a-url")]
    [InlineData("ftp://codex")]
    public void An_unusable_base_url_resolves_to_no_uri(string value)
    {
        var configuration = Build(new() { ["AiAccess:CodexBridgeBaseUrl"] = value });

        Assert.Null(CodexBridgeConfiguration.BaseUri(configuration));
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
