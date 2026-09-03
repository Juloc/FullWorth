namespace FullWorth.Web.Security.Headers;

public static class SecurityHeadersPolicy
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "style-src-attr 'unsafe-inline'; " +
        "img-src 'self' data: https://enablebanking.com https://*.enablebanking.com; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-src 'none'; " +
        "worker-src 'self'; " +
        "manifest-src 'self'; " +
        "media-src 'self';";

    public const string ReferrerPolicy = "strict-origin-when-cross-origin";

    public const string PermissionsPolicy =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=(), serial=(), " +
        "accelerometer=(), gyroscope=(), magnetometer=()";

    public static readonly TimeSpan HstsMaxAge = TimeSpan.FromDays(180);

    public static bool ShouldUseHsts(string environmentName) =>
        string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
}
