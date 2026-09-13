using FullWorth.Web.Modules.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Admin;

/// <summary>
/// The learned address has to beat the appsettings defaults, and that is decided by one number: where
/// the stored-settings source sits in the configuration chain.
///
/// Getting it wrong is silent until the next restart. The host pin is fail-closed in Production — an
/// instance with users and no pin refuses to start — so a source inserted one position too early
/// turns "the address it learned" into "the appsettings wildcard", and the container restart-loops on
/// <c>This instance has users but no host pin</c> with the address sitting right there in its own
/// database.
/// </summary>
public sealed class HostPinPrecedenceTests
{
    /// <summary>
    /// The whole point of the position, asserted against the real host's configuration rather than
    /// against a hand-built chain — the bug was in how the position is computed, so a test that
    /// rebuilds the chain itself would reproduce the intent and not the code.
    /// </summary>
    [Fact]
    public void A_learned_address_beats_the_appsettings_defaults()
    {
        using var factory = new FullWorthWebFactory();
        using (factory.CreateClient()) { }

        var source = factory.Services.GetRequiredService<InstancePublicUrlConfigurationSource>();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        source.Provider.Publish("https://web.fullworth.example");

        Assert.Equal("web.fullworth.example;127.0.0.1;localhost", configuration["AllowedHosts"]);
        Assert.Equal(
            "https://web.fullworth.example/connect/enable-banking/callback",
            configuration["EnableBanking:RedirectUrl"]);

        // And the other direction, which this factory happens to demonstrate: it states
        // Passkeys:RelyingPartyId itself, and that still wins. A value somebody wrote down is always
        // the deliberate one — the learned address only fills what nobody set.
        Assert.Equal("localhost", configuration["Passkeys:RelyingPartyId"]);
    }

    /// <summary>
    /// The same question the Production guard asks, in the same words: is there a pin, and is it more
    /// than the wildcard. This is the assertion that would have caught the restart loop.
    /// </summary>
    [Fact]
    public void After_learning_an_address_the_production_host_pin_guard_would_pass()
    {
        using var factory = new FullWorthWebFactory();
        using (factory.CreateClient()) { }

        var source = factory.Services.GetRequiredService<InstancePublicUrlConfigurationSource>();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        source.Provider.Publish("https://web.fullworth.example");

        var pinned = configuration["AllowedHosts"];
        Assert.False(string.IsNullOrWhiteSpace(pinned));
        Assert.DoesNotContain(pinned!.Split(';'), host => host.Trim() == "*");
    }

    /// <summary>
    /// And the other half of the position: an operator who states the value by hand still wins. The
    /// source sits below the environment variables, so an explicit one overrides what was learned.
    /// </summary>
    [Fact]
    public void The_stored_source_sits_below_the_environment_variables()
    {
        using var factory = new FullWorthWebFactory();
        using (factory.CreateClient()) { }

        var source = factory.Services.GetRequiredService<InstancePublicUrlConfigurationSource>();
        var root = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();

        var providers = root.Providers.ToList();
        var stored = providers.FindIndex(provider => ReferenceEquals(provider, source.Provider));
        var environment = providers.FindLastIndex(provider =>
            provider is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider);

        Assert.True(stored >= 0, "the stored-settings provider is not in the chain at all");
        Assert.True(
            environment < 0 || stored < environment,
            $"the stored provider is at {stored} and the environment variables at {environment}; "
            + "a stored value would silently override an explicitly configured one");

        // And above everything that came from a file, which is the half that broke.
        var json = providers.FindLastIndex(provider =>
            provider is Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider);
        Assert.True(
            json < 0 || stored > json,
            $"the stored provider is at {stored} and the last appsettings file at {json}; "
            + "appsettings would win, and its AllowedHosts is the wildcard the host pin refuses");
    }
}
