using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace FullWorth.Web.Tests.Sessions;

public sealed class SessionCookiePolicyTests
{
    [Fact]
    public void ProductionCookie_IsHttpOnlySecureAndLax()
    {
        var cookie = new CookieAuthenticationOptions();
        var sessions = new SessionOptions();

        SessionCookiePolicy.Apply(cookie, sessions, isProduction: true);

        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.Equal("/", cookie.Cookie.Path);
        // Production applies the __Host- prefix (P1.2b): browser then guarantees Secure + Path=/ + no Domain.
        Assert.Equal("__Host-" + SessionCookiePolicy.CookieName, cookie.Cookie.Name);
        Assert.Equal(sessions.IdleTimeout, cookie.ExpireTimeSpan);
        Assert.True(cookie.SlidingExpiration);
    }

    [Fact]
    public void DevelopmentCookie_DoesNotForceSecureFalse()
    {
        var cookie = new CookieAuthenticationOptions();

        SessionCookiePolicy.Apply(cookie, new SessionOptions(), isProduction: false);

        Assert.Equal(CookieSecurePolicy.SameAsRequest, cookie.Cookie.SecurePolicy);
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
    }

    [Fact]
    public void CookiePolicy_DoesNotCreatePermanentRememberMeLifetime()
    {
        var cookie = new CookieAuthenticationOptions();

        SessionCookiePolicy.Apply(cookie, new SessionOptions(), isProduction: true);

        Assert.Null(cookie.Cookie.MaxAge);
    }
}
