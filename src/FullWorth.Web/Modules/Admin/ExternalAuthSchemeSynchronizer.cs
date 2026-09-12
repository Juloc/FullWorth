using Indice.AspNetCore.Authentication.Apple;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// Keeps the external sign-in schemes in step with what is actually configured.
///
/// A registered scheme is not free. Google and Apple are REMOTE handlers, so ASP.NET asks every one of
/// them on every single request whether it wants to handle the path — and building a handler validates
/// its options. An unconfigured provider therefore does not sit quietly unused: it throws
/// <c>ArgumentNullException (Parameter 'ClientId')</c> out of the authentication middleware, and the
/// whole instance answers 500 to everything, including static files. That is exactly what happened when
/// the providers were first registered unconditionally.
///
/// So the scheme exists exactly while its credentials do. That is also what makes the admin surface work
/// without a restart: saving credentials adds the scheme back, clearing them removes it.
/// </summary>
public sealed class ExternalAuthSchemeSynchronizer(
    IAuthenticationSchemeProvider schemes,
    ExternalAuthOptionsResolver resolver,
    IOptionsMonitorCache<GoogleOptions> googleCache,
    IOptionsMonitorCache<AppleOptions> appleCache)
{
    // Remembered from the registration itself rather than named here. The Apple provider is built on
    // OpenID Connect and has no type called "AppleHandler"; hardcoding a guess would break on a package
    // update in a way no test would catch until a login stopped working.
    private readonly Dictionary<string, Type> handlerTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Brings both schemes in line with the current configuration and stored settings.
    ///
    /// The options caches are cleared first: options for an auth scheme are built once and kept for the
    /// life of the process, so without this the first sign-in after a change would still use the previous
    /// credentials — and an admin would have every reason to think saving did nothing.
    /// </summary>
    public async Task SynchronizeAsync()
    {
        googleCache.TryRemove(ExternalAuthOptionsResolver.GoogleScheme);
        appleCache.TryRemove(ExternalAuthOptionsResolver.AppleScheme);

        await ApplyAsync(ExternalAuthOptionsResolver.GoogleScheme, "Google");
        await ApplyAsync(ExternalAuthOptionsResolver.AppleScheme, "Apple");
    }

    private async Task ApplyAsync(string scheme, string displayName)
    {
        var registered = await schemes.GetSchemeAsync(scheme);
        if (registered is not null) handlerTypes[scheme] = registered.HandlerType;

        var configured = resolver.IsConfigured(scheme);

        if (configured && registered is null && handlerTypes.TryGetValue(scheme, out var handlerType))
            schemes.AddScheme(new AuthenticationScheme(scheme, displayName, handlerType));
        else if (!configured && registered is not null)
            schemes.RemoveScheme(scheme);
    }
}
