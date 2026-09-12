using FullWorth.Web.Data;
using FullWorth.Web.Modules.Admin;
using FullWorth.Web.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Admin;

/// <summary>
/// The installation learns the address it is reached at, instead of being told.
///
/// It was the last line a deployment had to edit by hand, and three more were derived from it — the
/// passkey relying party and origin, the Enable Banking redirect and the host pin. The app can simply
/// learn it: the first registration happens on the real domain, through the real reverse proxy, with a
/// human present and not one passkey registered yet.
/// </summary>
public sealed class InstancePublicUrlTests
{
    /// <summary>
    /// Create-only, and this is the one that matters. The passkey relying party id is derived from the
    /// address, and changing a relying party id makes every passkey already registered against it
    /// unusable — silently, at the next login attempt.
    /// </summary>
    [Fact]
    public async Task The_address_is_remembered_once_and_never_changed()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
        var store = scope.ServiceProvider.GetRequiredService<InstanceSettingsStore>();

        var first = await store.RememberPublicUrlAsync("https://web.fullworth.de", CancellationToken.None);
        var second = await store.RememberPublicUrlAsync("https://someone-elses.example", CancellationToken.None);

        Assert.Equal("https://web.fullworth.de", first);
        Assert.Equal("https://web.fullworth.de", second);
        Assert.Equal("https://web.fullworth.de", await store.GetPublicUrlAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_installation_that_has_never_been_used_has_no_address()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<InstanceSettingsStore>()
            .GetPublicUrlAsync(CancellationToken.None));
    }

    /// <summary>
    /// Publishing the address is what makes the four derived settings appear — including the host pin,
    /// which the host-filtering middleware reads through an options monitor bound to configuration. That
    /// is why this is a configuration source and not a service: the pin starts applying the moment the
    /// address is learned, with no restart.
    /// </summary>
    [Fact]
    public void Publishing_the_address_produces_every_derived_setting()
    {
        var source = new InstancePublicUrlConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();

        source.Provider.Publish("https://web.fullworth.de");

        Assert.Equal("web.fullworth.de", configuration["Passkeys:RelyingPartyId"]);
        Assert.Equal("https://web.fullworth.de", configuration["Passkeys:Origins:0"]);
        Assert.Equal(
            "https://web.fullworth.de/connect/enable-banking/callback",
            configuration["EnableBanking:RedirectUrl"]);
        Assert.Equal("web.fullworth.de;127.0.0.1;localhost", configuration["AllowedHosts"]);
        // The key spelled out: FullWorth.Shared.PublicUrl is compiled into both the Web and the
        // Banking assembly, so naming the constant here is ambiguous - and the key is part of the
        // contract with a deployment anyway.
        Assert.Equal("https://web.fullworth.de", configuration["FullWorth:PublicUrl"]);
    }

    /// <summary>
    /// Nothing to publish publishes NOTHING - in particular no host pin. An absent pin and a pin of "*"
    /// are different things: the first makes a deployment with users refuse to start, the second would
    /// quietly let every host through.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void Without_an_address_no_host_pin_is_invented(string? address)
    {
        var source = new InstancePublicUrlConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();

        source.Provider.Publish(address);

        Assert.Null(configuration["AllowedHosts"]);
        Assert.Null(configuration["Passkeys:RelyingPartyId"]);
    }

    /// <summary>
    /// A later address replaces the earlier one in configuration rather than piling up next to it — the
    /// stale relying party would otherwise stay readable and could be picked up.
    /// </summary>
    [Fact]
    public void Publishing_again_replaces_what_was_published_before()
    {
        var source = new InstancePublicUrlConfigurationSource();
        var configuration = new ConfigurationBuilder().Add(source).Build();

        source.Provider.Publish("https://first.example");
        source.Provider.Publish(null);

        Assert.Null(configuration["Passkeys:RelyingPartyId"]);
    }
}
