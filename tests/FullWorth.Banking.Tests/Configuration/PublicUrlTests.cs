using FullWorth.Shared;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Banking.Tests.Configuration;

/// <summary>
/// One public address, and the four settings that follow from it.
///
/// A deploy stack used to spell all four out side by side — the passkey relying party id, the passkey
/// origin, the Enable Banking redirect url and the host pin. Four lines, one fact, and four ways to get
/// it subtly wrong. The worst is quiet: set the relying party id, forget the origin, and passkey
/// registration fails the origin check in the browser with nothing in the server log to explain it.
/// </summary>
public sealed class PublicUrlTests
{
    [Fact]
    public void All_four_settings_are_derived_from_the_one_address()
    {
        var configuration = Manager(new() { [PublicUrl.Key] = "https://web.fullworth.de" });

        PublicUrl.AddDerivedSettings(configuration);

        // A relying party id is a bare host. An origin needs the scheme. Writing both out by hand is
        // exactly where that distinction gets lost.
        Assert.Equal("web.fullworth.de", configuration["Passkeys:RelyingPartyId"]);
        Assert.Equal("https://web.fullworth.de", configuration["Passkeys:Origins:0"]);
        Assert.Equal(
            "https://web.fullworth.de/connect/enable-banking/callback",
            configuration["EnableBanking:RedirectUrl"]);
        Assert.Equal("web.fullworth.de;127.0.0.1;localhost", configuration["AllowedHosts"]);
    }

    /// <summary>
    /// Loopback stays allowed next to the public host. The container's own healthcheck asks
    /// <c>http://localhost:8080/health</c>, so pinning the public host alone would fail it — and a
    /// container that reports unhealthy is restarted forever.
    /// </summary>
    [Fact]
    public void The_host_pin_still_lets_the_containers_own_healthcheck_through()
    {
        var configuration = Manager(new() { [PublicUrl.Key] = "https://web.fullworth.de" });

        PublicUrl.AddDerivedSettings(configuration);

        Assert.Contains("localhost", configuration["AllowedHosts"]);
        Assert.Contains("127.0.0.1", configuration["AllowedHosts"]);
    }

    /// <summary>A value written by hand is the deliberate one and is never overwritten — per key.</summary>
    [Fact]
    public void An_explicitly_configured_setting_wins()
    {
        var configuration = Manager(new()
        {
            [PublicUrl.Key] = "https://web.fullworth.de",
            ["Passkeys:RelyingPartyId"] = "legacy.example.org"
        });

        PublicUrl.AddDerivedSettings(configuration);

        Assert.Equal("legacy.example.org", configuration["Passkeys:RelyingPartyId"]);
        // The others are still derived: this is per key, not all-or-nothing.
        Assert.Equal("https://web.fullworth.de", configuration["Passkeys:Origins:0"]);
    }

    /// <summary>
    /// A port is part of an origin but never part of a relying party id. Getting that backwards makes
    /// every passkey on the instance unusable.
    /// </summary>
    [Fact]
    public void A_port_belongs_to_the_origin_and_not_to_the_relying_party()
    {
        var configuration = Manager(new() { [PublicUrl.Key] = "https://fullworth.local:8443" });

        PublicUrl.AddDerivedSettings(configuration);

        Assert.Equal("fullworth.local", configuration["Passkeys:RelyingPartyId"]);
        Assert.Equal("https://fullworth.local:8443", configuration["Passkeys:Origins:0"]);
    }

    /// <summary>A bare hostname is what an operator types; https is the only sane assumption.</summary>
    [Theory]
    [InlineData("web.fullworth.de", "https://web.fullworth.de")]
    [InlineData("https://web.fullworth.de/", "https://web.fullworth.de")]
    [InlineData("  https://web.fullworth.de  ", "https://web.fullworth.de")]
    public void The_address_is_accepted_the_way_a_person_writes_it(string configured, string expectedOrigin)
    {
        var configuration = Manager(new() { [PublicUrl.Key] = configured });

        PublicUrl.AddDerivedSettings(configuration);

        Assert.Equal(expectedOrigin, configuration["Passkeys:Origins:0"]);
    }

    /// <summary>
    /// Nothing configured derives nothing. In particular it must NOT invent an AllowedHosts value: that
    /// is the fail-closed host pin, and a wrong one either locks everybody out or silently unpins it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    public void Without_a_usable_address_nothing_is_derived(string? configured)
    {
        var configuration = Manager(new() { [PublicUrl.Key] = configured });

        PublicUrl.AddDerivedSettings(configuration);

        Assert.Null(configuration["Passkeys:RelyingPartyId"]);
        Assert.Null(configuration["AllowedHosts"]);
    }

    /// <summary>
    /// The wildcard counts as UNSET for the host pin, unlike every other setting here.
    ///
    /// appsettings.json ships <c>"AllowedHosts": "*"</c> and that is also ASP.NET's own default - nobody
    /// types it to mean "pin the host to anything". Treating it as somebody's deliberate value made this
    /// derivation dead code for the one setting that matters most: the pin stayed "*" in the shipped
    /// image no matter what the public address said, and only the Production startup guard caught it.
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData(" * ")]
    [InlineData("web.fullworth.de;*")]
    public void A_wildcard_host_pin_counts_as_not_pinned_at_all(string shipped)
    {
        var configuration = Manager(new()
        {
            [PublicUrl.Key] = "https://web.fullworth.de",
            ["AllowedHosts"] = shipped
        });

        PublicUrl.AddDerivedSettings(configuration);

        Assert.Equal("web.fullworth.de;127.0.0.1;localhost", configuration["AllowedHosts"]);
    }

    /// <summary>But a real pin somebody wrote is still theirs.</summary>
    [Fact]
    public void A_real_host_pin_is_left_alone()
    {
        var configuration = Manager(new()
        {
            [PublicUrl.Key] = "https://web.fullworth.de",
            ["AllowedHosts"] = "legacy.example.org;localhost"
        });

        PublicUrl.AddDerivedSettings(configuration);

        Assert.Equal("legacy.example.org;localhost", configuration["AllowedHosts"]);
    }

    private static ConfigurationManager Manager(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        return configuration;
    }
}
