using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace FullWorth.Web.Modules.Sessions;

public static class SessionCookiePolicy
{
    public const string CookieName = "Finance.Auth";

    public static void Apply(CookieAuthenticationOptions cookie, SessionOptions sessions, bool isProduction)
    {
        sessions.Validate();

        // In Production use the __Host- prefix (P1.2b): the browser then guarantees Secure + Path=/ +
        // no Domain, which this policy already satisfies. Kept unprefixed in dev where Secure is relaxed.
        cookie.Cookie.Name = isProduction ? "__Host-" + CookieName : CookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.Path = "/";
        cookie.Cookie.IsEssential = true;
        cookie.Cookie.SecurePolicy = isProduction
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = sessions.IdleTimeout;
        cookie.SlidingExpiration = true;
        cookie.LoginPath = "/auth/login";
        cookie.AccessDeniedPath = "/auth/access-denied";
    }
}
