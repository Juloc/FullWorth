using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Security.Antiforgery;

public static class FullWorthAntiforgeryServiceCollectionExtensions
{
    public static IServiceCollection AddFullWorthAntiforgery(
        this IServiceCollection services,
        bool secureCookie)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = FullWorthAntiforgeryDefaults.HeaderName;
            options.Cookie.Name = FullWorthAntiforgeryDefaults.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = secureCookie
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
        });

        return services;
    }
}
