using Indice.AspNetCore.Authentication.Apple;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// Fills the external sign-in providers from the stored settings, falling back to configuration.
///
/// The providers used to be registered at startup only if configuration happened to carry their
/// credentials, which meant a deploy stack had to pass six environment variables and a restart was the
/// only way to turn a login button on. They are registered unconditionally now and get their values
/// here — so an admin saving them takes effect on the next sign-in attempt, with no restart.
///
/// Configuration still wins. An operator who prefers environment variables keeps exactly what they had.
///
/// Reads through a scope because this runs as a singleton (that is how options configuration works) and
/// the store needs a DbContext. The result is cached by <c>IOptionsMonitorCache</c>, which
/// <see cref="ExternalAuthSettingsEndpoints"/> evicts on save — otherwise the first sign-in after a
/// change would still use the old credentials, for the lifetime of the process.
/// </summary>
public sealed class ExternalAuthOptionsResolver(
    IServiceScopeFactory scopes,
    IConfiguration configuration)
    : IConfigureNamedOptions<GoogleOptions>, IConfigureNamedOptions<AppleOptions>
{
    public const string GoogleScheme = "Google";
    public const string AppleScheme = "Apple";

    public void Configure(string? name, GoogleOptions options)
    {
        if (name != GoogleScheme) return;

        // Configuration first: it is what an operator wrote down deliberately, and it is also the only
        // source available before the database is reachable.
        var clientId = configuration["ExternalAuth:Google:ClientId"];
        var clientSecret = configuration["ExternalAuth:Google:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            var stored = Read(store => store.GetGoogleAsync(CancellationToken.None));
            if (stored is null) return;
            clientId = stored.ClientId;
            clientSecret = stored.ClientSecret;
        }

        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
    }

    public void Configure(string? name, AppleOptions options)
    {
        if (name != AppleScheme) return;

        var serviceId = configuration["ExternalAuth:Apple:ServiceId"];
        var teamId = configuration["ExternalAuth:Apple:TeamId"];
        var privateKeyId = configuration["ExternalAuth:Apple:PrivateKeyId"];
        var privateKey = DecodeBase64(configuration["ExternalAuth:Apple:PrivateKeyBase64"]);

        if (string.IsNullOrWhiteSpace(serviceId) || string.IsNullOrWhiteSpace(teamId)
            || string.IsNullOrWhiteSpace(privateKeyId) || string.IsNullOrWhiteSpace(privateKey))
        {
            var stored = Read(store => store.GetAppleAsync(CancellationToken.None));
            if (stored is null) return;
            serviceId = stored.ServiceId;
            teamId = stored.TeamId;
            privateKeyId = stored.PrivateKeyId;
            privateKey = stored.PrivateKey;
        }

        options.ServiceId = serviceId;
        options.TeamId = teamId;
        options.PrivateKeyId = privateKeyId;
        options.PrivateKey = privateKey;
    }

    public void Configure(GoogleOptions options) => Configure(GoogleScheme, options);

    public void Configure(AppleOptions options) => Configure(AppleScheme, options);

    /// <summary>
    /// Whether a scheme has everything it needs. The sign-in endpoint asks before challenging: an
    /// unconfigured provider would otherwise throw out of the handler as a 500, which reads like the app
    /// is broken rather than like a provider nobody set up.
    /// </summary>
    public bool IsConfigured(string scheme) => scheme switch
    {
        GoogleScheme => !string.IsNullOrWhiteSpace(
            configuration["ExternalAuth:Google:ClientId"]) &&
            !string.IsNullOrWhiteSpace(configuration["ExternalAuth:Google:ClientSecret"])
            || Read(store => store.GetGoogleAsync(CancellationToken.None)) is not null,
        AppleScheme => !string.IsNullOrWhiteSpace(
            configuration["ExternalAuth:Apple:ServiceId"]) &&
            !string.IsNullOrWhiteSpace(configuration["ExternalAuth:Apple:PrivateKeyBase64"])
            || Read(store => store.GetAppleAsync(CancellationToken.None)) is not null,
        _ => false
    };

    /// <summary>
    /// Reads from the store in its own scope. Never throws: options configuration runs while the auth
    /// handler is being built, and a database that is momentarily unreachable must not turn every request
    /// into a 500 - "no provider configured" is the honest degradation.
    /// </summary>
    private T? Read<T>(Func<ExternalAuthSettingsStore, Task<T?>> read) where T : class
    {
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ExternalAuthSettingsStore>();
            return read(store).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    internal static string? DecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim())); }
        catch (FormatException) { return null; }
    }
}
