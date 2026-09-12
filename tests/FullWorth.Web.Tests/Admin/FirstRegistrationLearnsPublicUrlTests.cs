using System.Net;
using System.Net.Http.Json;
using FullWorth.Web.Modules.Admin;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Admin;

/// <summary>
/// The first registration is where the installation learns the address it is reached at.
///
/// That address used to be a compose line, with three more derived from it — the passkey relying party
/// and origin, the Enable Banking redirect and the host pin. The first registration is the one honest
/// moment to learn it instead: a human is here, on the real domain, through the real reverse proxy, and
/// not one passkey exists yet whose relying party id could be invalidated by getting it wrong.
/// </summary>
public sealed class FirstRegistrationLearnsPublicUrlTests
{
    [Fact]
    public async Task Registering_the_first_user_teaches_the_instance_its_address()
    {
        await using var factory = new FullWorthWebFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Host = "web.fullworth.example";

        await using (var before = factory.Services.CreateAsyncScope())
            Assert.Null(await before.ServiceProvider.GetRequiredService<InstanceSettingsStore>()
                .GetPublicUrlAsync(CancellationToken.None));

        using var response = await client.PostAsJsonAsync("/auth/register", new
        {
            email = $"first-{Guid.NewGuid():N}@example.com",
            displayName = "First Admin",
            password = "correct-horse-battery-staple-1",
            acceptTerms = true,
            confirmAdult = true
        });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var learned = await scope.ServiceProvider.GetRequiredService<InstanceSettingsStore>()
            .GetPublicUrlAsync(CancellationToken.None);
        Assert.Equal("http://web.fullworth.example", learned);

        // And it is in force immediately, not after a restart: the source published the derived
        // settings, which is what the host-filtering middleware re-binds to.
        //
        // Asserted on the SOURCE, not on the merged configuration: this harness sets its own
        // Passkeys:RelyingPartyId through an in-memory source that sits above this one, and that
        // precedence is deliberate - a value an operator supplies has to keep winning.
        var published = new ConfigurationBuilder()
            .Add(scope.ServiceProvider.GetRequiredService<InstancePublicUrlConfigurationSource>())
            .Build();
        Assert.Equal("web.fullworth.example", published["Passkeys:RelyingPartyId"]);
        Assert.Equal("web.fullworth.example;127.0.0.1;localhost", published["AllowedHosts"]);
        Assert.Equal(
            "http://web.fullworth.example/connect/enable-banking/callback",
            published["EnableBanking:RedirectUrl"]);
    }

    /// <summary>
    /// A second registration does not move it. The relying party id is derived from this address, and
    /// changing one makes every passkey registered against it unusable — silently, at the next login.
    /// </summary>
    [Fact]
    public async Task A_later_registration_on_another_host_does_not_move_the_address()
    {
        await using var factory = new FullWorthWebFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        client.DefaultRequestHeaders.Host = "web.fullworth.example";
        using (var first = await client.PostAsJsonAsync("/auth/register", Registration()))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Self-service registration is off by default after the first user, so this one is refused —
        // which is exactly the point: there is no second chance to teach the instance a new address.
        client.DefaultRequestHeaders.Host = "someone-elses.example";
        using (var second = await client.PostAsJsonAsync("/auth/register", Registration()))
            Assert.NotEqual(HttpStatusCode.OK, second.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(
            "http://web.fullworth.example",
            await scope.ServiceProvider.GetRequiredService<InstanceSettingsStore>()
                .GetPublicUrlAsync(CancellationToken.None));
    }

    private static object Registration() => new
    {
        email = $"user-{Guid.NewGuid():N}@example.com",
        displayName = "Someone",
        password = "correct-horse-battery-staple-1",
        acceptTerms = true,
        confirmAdult = true
    };
}
