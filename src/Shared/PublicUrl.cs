using Microsoft.Extensions.Configuration;

namespace FullWorth.Shared;

/// <summary>
/// The one public address this installation is reached at, and the four settings that follow from it.
///
/// A deploy stack used to spell all four out, side by side:
///
/// <code>
///   EnableBanking__RedirectUrl: https://web.fullworth.de/connect/enable-banking/callback
///   Passkeys__RelyingPartyId:   web.fullworth.de
///   Passkeys__Origins__0:       https://web.fullworth.de
///   AllowedHosts:               web.fullworth.de;127.0.0.1;localhost
/// </code>
///
/// Four lines, one fact, and four ways to get it subtly wrong. The worst is quiet: set the relying party
/// id and forget the origin, and passkey registration fails the origin check at the browser with nothing
/// in the server log to explain it. The redirect url has to match what is registered with Enable Banking
/// to the character, and <c>AllowedHosts</c> is the fail-closed host pin that refuses to start on "*".
///
/// So: <c>FullWorth:PublicUrl</c> is the single value, and the rest are derived. An explicitly configured
/// value still wins for every one of them, so an existing deployment and every test keep working.
/// </summary>
public static class PublicUrl
{
    public const string Key = "FullWorth:PublicUrl";

    /// <summary>
    /// Overlays the four derived settings. Call once, while building configuration, and only for values
    /// nobody set by hand.
    /// </summary>
    public static void AddDerivedSettings(IConfigurationManager configuration)
    {
        if (!TryParse(configuration[Key], out var origin, out var host)) return;

        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Fill(configuration, overlay, "Passkeys:RelyingPartyId", host);
        Fill(configuration, overlay, "Passkeys:Origins:0", origin);
        Fill(configuration, overlay, "EnableBanking:RedirectUrl", origin + "/connect/enable-banking/callback");

        // Loopback stays allowed alongside the public host: the container's own healthcheck asks
        // http://localhost:8080/health, and pinning the public host alone would fail it.
        //
        // "*" counts as unset here, unlike everywhere else. It is what appsettings.json ships and what
        // ASP.NET defaults to - nobody types it to mean "pin the host to anything". Treating it as a
        // deliberate value made this derivation dead code for the one setting that matters most: the
        // host pin stayed "*" no matter what FullWorth:PublicUrl said.
        if (IsUnpinned(configuration["AllowedHosts"]))
            overlay["AllowedHosts"] = $"{host};127.0.0.1;localhost";

        if (overlay.Count > 0) configuration.AddInMemoryCollection(overlay);
    }

    /// <summary>
    /// Splits the configured URL into the origin ("https://web.fullworth.de") and the host
    /// ("web.fullworth.de"), or returns false when there is nothing usable to derive from.
    ///
    /// A relying party id is a bare host - no scheme, no port, no path - while an origin needs the
    /// scheme and, when it is not the default, the port. Getting that distinction wrong is exactly the
    /// kind of mistake writing it out four times invites.
    /// </summary>
    public static bool TryParse(string? configured, out string origin, out string host)
    {
        origin = string.Empty;
        host = string.Empty;

        var value = configured?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;

        host = uri.Host;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return host.Length > 0;
    }

    /// <summary>
    /// Whether the host pin is absent in the only sense that matters: missing, or the wildcard that
    /// pins nothing. Production refuses to start on either.
    /// </summary>
    private static bool IsUnpinned(string? allowedHosts) =>
        string.IsNullOrWhiteSpace(allowedHosts) ||
        allowedHosts.Split(';').Any(host => host.Trim() == "*");

    /// <summary>Only fills a key nobody set: a hand-written value is always the deliberate one.</summary>
    private static void Fill(
        IConfiguration configuration, Dictionary<string, string?> overlay, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(configuration[key])) overlay[key] = value;
    }
}
