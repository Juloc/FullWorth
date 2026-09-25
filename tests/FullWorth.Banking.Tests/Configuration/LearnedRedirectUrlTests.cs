using FullWorth.Banking.EnableBanking;
using FullWorth.Banking.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests.Configuration;

/// <summary>
/// The address this installation LEARNS has to reach the code that uses it.
///
/// It did not. <c>EnableBanking:RedirectUrl</c> has been derived from the public URL since the
/// installation started learning that address at its first registration — but every consumer took
/// <c>IOptions&lt;EnableBankingOptions&gt;</c> and cached <c>.Value</c> in a readonly field.
/// <c>IOptions&lt;T&gt;</c> is a process-lifetime snapshot, so the value that arrived after startup
/// was only ever picked up by a restart. Two of those consumers are singletons, which made the
/// snapshot the one taken at process start, for good.
///
/// <c>AllowedHosts</c> worked the whole time and hid it: HostFilteringMiddleware reads through an
/// options monitor, so the pin applied immediately while the redirect url silently did not.
/// </summary>
public sealed class LearnedRedirectUrlTests
{
    /// <summary>
    /// The mechanism, at the level the bug lived at: a reloading configuration source, real options
    /// binding, and a consumer that must see the new value without being rebuilt.
    /// </summary>
    [Fact]
    public void A_reloaded_configuration_reaches_the_options_monitor_without_a_restart()
    {
        var learned = new ReloadableSource();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableBanking:RedirectUrl"] = "https://placeholder.invalid/connect/enable-banking/callback"
            })
            .Add(learned)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<EnableBankingOptions>(
            configuration.GetSection(EnableBankingOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var monitor = provider.GetRequiredService<IOptionsMonitor<EnableBankingOptions>>();
        Assert.Equal(
            "https://placeholder.invalid/connect/enable-banking/callback",
            monitor.CurrentValue.RedirectUrl);

        // Resolved BEFORE the address is learned, which is what the old code did: the services were
        // constructed at startup. IOptions<T> binds lazily, so resolving it afterwards would quietly
        // hand back the new value and prove nothing.
        var snapshotHeldSinceStartup = provider.GetRequiredService<IOptions<EnableBankingOptions>>();
        _ = snapshotHeldSinceStartup.Value;

        // What the first registration does: publish the learned address into a source that sits above
        // appsettings, then reload.
        learned.Publish("https://web.fullworth.example/connect/enable-banking/callback");

        Assert.Equal(
            "https://web.fullworth.example/connect/enable-banking/callback",
            monitor.CurrentValue.RedirectUrl);

        // And the snapshot is still the startup value. This is the bug, in one assertion.
        Assert.Equal(
            "https://placeholder.invalid/connect/enable-banking/callback",
            snapshotHeldSinceStartup.Value.RedirectUrl);
    }

    /// <summary>
    /// And the consumers themselves. A field initialised from <c>.CurrentValue</c> would pass the test
    /// above and still be broken — the value has to be read at the moment it is used.
    /// </summary>
    [Fact]
    public void The_control_panel_services_read_the_address_at_the_moment_they_use_it()
    {
        var options = StaticOptionsMonitor.For(new EnableBankingOptions
        {
            RedirectUrl = "https://old.example/connect/enable-banking/callback"
        });

        var resolver = new EnableBankingClientResolver(
            new StubHttpClientFactory(),
            options,
            new EnableBankingRequestPolicy(),
            backend: null!,
            TimeProvider.System);

        Assert.False(resolver.LegacyConfigured);   // no application id yet

        options.Set(new EnableBankingOptions
        {
            RedirectUrl = "https://new.example/connect/enable-banking/callback",
            ApplicationId = "an-application",
            PrivateKeyBase64 = "not-empty"
        });

        // Reading the new value at all is the assertion: with the old cached field this stayed false
        // for the life of the process.
        Assert.True(resolver.LegacyConfigured);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>The shape of InstancePublicUrlProvider, reduced to what this test needs.</summary>
    private sealed class ReloadableSource : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public void Publish(string redirectUrl)
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["EnableBanking:RedirectUrl"] = redirectUrl
            };
            OnReload();
        }
    }
}
