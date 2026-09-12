using FullWorth.Shared;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// The instance's public address, stored rather than deployed, as a reloadable configuration source.
///
/// It used to be a compose line, and it was the last one an operator had to edit per deployment. It is
/// not a credential and not an id — it is "which address am I reached at", and the app learns it the
/// moment a human first uses it: the first registration happens on the real domain, through the real
/// reverse proxy.
///
/// A configuration SOURCE rather than a service, because of what consumes it. <c>AllowedHosts</c> is read
/// by the host-filtering middleware the generic host installs, through
/// <c>IOptionsMonitor&lt;HostFilteringOptions&gt;</c> bound to configuration with a change token. Feeding
/// it from here means the pin starts applying the moment it is learned, with no restart and no middleware
/// of our own.
///
/// The four derived keys are emitted directly (see <see cref="PublicUrl"/>), because a reload has to
/// produce them again — deriving once while building configuration would only ever run at startup.
/// </summary>
public sealed class InstancePublicUrlConfigurationSource : IConfigurationSource
{
    public InstancePublicUrlProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class InstancePublicUrlProvider : ConfigurationProvider
{
    /// <summary>
    /// Publishes the address and everything derived from it, then signals the change so bound options
    /// re-bind. Null or blank publishes nothing at all: an absent pin is a different thing from a pin of
    /// "*", and inventing one here would silently unpin a deployment.
    /// </summary>
    public void Publish(string? publicUrl)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (PublicUrl.TryParse(publicUrl, out var origin, out var host))
        {
            data[PublicUrl.Key] = origin;
            data["Passkeys:RelyingPartyId"] = host;
            data["Passkeys:Origins:0"] = origin;
            data["EnableBanking:RedirectUrl"] = origin + "/connect/enable-banking/callback";
            // Loopback stays allowed: the container's own healthcheck asks http://localhost:8080/health,
            // and a container that reports unhealthy is restarted forever.
            data["AllowedHosts"] = $"{host};127.0.0.1;localhost";
        }

        Data = data;
        OnReload();
    }

    /// <summary>Nothing to load from: the value arrives through <see cref="Publish"/>.</summary>
    public override void Load() { }
}
