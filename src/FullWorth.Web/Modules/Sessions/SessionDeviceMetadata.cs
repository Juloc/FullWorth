using System.Net;

namespace FullWorth.Web.Modules.Sessions;

public sealed record SessionDeviceMetadata(string DeviceName, string? UserAgent, string? IpAddress)
{
    public static SessionDeviceMetadata Create(string? userAgent, string? ipAddress)
    {
        var safeUserAgent = Sanitize(userAgent, UserSession.MaxUserAgentLength);
        var safeIpAddress = NormalizeIpAddress(ipAddress);
        return new SessionDeviceMetadata(DeriveDeviceName(safeUserAgent), safeUserAgent, safeIpAddress);
    }

    private static string DeriveDeviceName(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Browser session";

        var browser = userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "Browser";

        var platform = userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
            : userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
            : userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : userAgent.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "macOS"
            : userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : "device";

        return Truncate($"{browser} on {platform}", UserSession.MaxDeviceNameLength)!;
    }

    private static string? NormalizeIpAddress(string? value)
    {
        var candidate = Sanitize(value, UserSession.MaxIpAddressLength);
        return candidate is not null && IPAddress.TryParse(candidate, out var parsed)
            ? parsed.ToString()
            : null;
    }

    private static string? Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return Truncate(clean, maxLength);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } ? value[..Math.Min(value.Length, maxLength)] : null;
}
