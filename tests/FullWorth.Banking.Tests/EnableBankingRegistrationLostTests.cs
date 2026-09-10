using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

/// <summary>
/// A pending automatic registration lives in memory for twenty minutes, on purpose: it holds a
/// control-panel refresh token and a freshly generated private key, and neither belongs in the database
/// for the sake of a wizard step somebody can simply start again.
///
/// But a restart then left the id unknown, the status poll answered 404, and the wizard kept polling that
/// 404 for as long as the page stayed open instead of failing the step and saying so.
/// </summary>
public sealed class EnableBankingRegistrationLostTests
{
    [Fact]
    public void An_unknown_registration_is_reported_as_expired_rather_than_missing()
    {
        var service = CreateService();

        var view = service.GetStatus(Guid.NewGuid(), "a-registration-this-process-never-had");

        Assert.Equal("expired", view.Status);
        Assert.Equal("registration_lost", view.ErrorCode);
        Assert.False(view.CanRetryVerification);
        Assert.Null(view.ApplicationId);
    }

    // Another user's id must look exactly like an id that does not exist - the answer may not reveal
    // that someone else has a registration in flight.
    [Fact]
    public void Another_users_registration_is_indistinguishable_from_an_unknown_one()
    {
        var service = CreateService();

        var mine = service.GetStatus(Guid.NewGuid(), "shared-id");
        var theirs = service.GetStatus(Guid.NewGuid(), "shared-id");

        Assert.Equal(mine.Status, theirs.Status);
        Assert.Equal(mine.ErrorCode, theirs.ErrorCode);
    }

    private static EnableBankingControlPanelRegistrationService CreateService()
    {
        var options = Options.Create(new EnableBankingOptions
        {
            BaseUrl = "https://api.enablebanking.test",
            ControlPanelBaseUrl = "https://enablebanking.test",
            RedirectUrl = "https://fullworth.test/connect/enable-banking/callback",
            ApplicationName = "FullWorth"
        });

        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        return new EnableBankingControlPanelRegistrationService(
            new NoHttpClientFactory(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<EnableBankingControlPanelRegistrationService>.Instance);
    }

    // Reading a status must not touch the network at all; a factory that refuses to hand out a client
    // proves it.
    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Reading a registration status must not call out.");
    }
}
