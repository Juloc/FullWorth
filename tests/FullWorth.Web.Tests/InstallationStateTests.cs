using System.Text.Json;
using FullWorth.Web.Modules.Landing;
using Microsoft.AspNetCore.Http;

namespace FullWorth.Web.Tests;

public sealed class InstallationStateTests
{
    private static JsonElement Sanitized(InstallationState state) =>
        JsonSerializer.SerializeToElement(state.ToSanitizedPayload());

    [Fact]
    public void Single_user_initialized_disabled_serializes_to_the_documented_contract()
    {
        var json = Sanitized(new InstallationState(InstallationMode.SingleUser, Initialized: true, RegistrationMode.Disabled));

        Assert.Equal("singleUser", json.GetProperty("mode").GetString());
        Assert.True(json.GetProperty("initialized").GetBoolean());
        Assert.Equal("disabled", json.GetProperty("registration").GetString());
    }

    [Fact]
    public void Multi_user_states_expose_camelcase_registration_modes()
    {
        Assert.Equal("multiUser",
            Sanitized(new InstallationState(InstallationMode.MultiUser, false, RegistrationMode.Open)).GetProperty("mode").GetString());
        Assert.Equal("open",
            Sanitized(new InstallationState(InstallationMode.MultiUser, false, RegistrationMode.Open)).GetProperty("registration").GetString());
        Assert.Equal("inviteOnly",
            Sanitized(new InstallationState(InstallationMode.MultiUser, true, RegistrationMode.InviteOnly)).GetProperty("registration").GetString());
    }

    [Fact]
    public void Sanitized_payload_leaks_no_user_counts_or_identities()
    {
        var json = Sanitized(new InstallationState(InstallationMode.MultiUser, true, RegistrationMode.InviteOnly));

        // Exactly the three documented keys — nothing that could carry a count or an identity.
        var names = json.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "initialized", "mode", "registration" }, names);
    }

    [Fact]
    public void Installation_defaults_are_the_safest_choice_for_a_fresh_self_host()
    {
        var options = new InstallationOptions();
        Assert.Equal(InstallationMode.SingleUser, options.Mode);
        Assert.Equal(RegistrationMode.Disabled, options.Registration);
    }

    [Fact]
    public async Task Default_landing_provider_sends_anonymous_visitors_to_sign_in()
    {
        var context = new DefaultHttpContext();
        var state = new InstallationState(InstallationMode.SingleUser, true, RegistrationMode.Disabled);

        await new DefaultLandingPageProvider().RenderAsync(context, state, CancellationToken.None);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/auth/login", context.Response.Headers.Location);
    }
}
