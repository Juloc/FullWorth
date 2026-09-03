using Microsoft.AspNetCore.HttpsPolicy;

namespace FullWorth.Web.Security.Headers;

public static class SecurityHeadersApplicationBuilderExtensions
{
    public static IServiceCollection AddFinanceSecurityHeaders(
        this IServiceCollection services,
        Action<SecurityHeadersOptions>? configure = null)
    {
        services.AddOptions<SecurityHeadersOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.Configure<HstsOptions>(options =>
        {
            options.MaxAge = SecurityHeadersPolicy.HstsMaxAge;
            options.IncludeSubDomains = false;
            options.Preload = false;
        });

        return services;
    }

    public static IApplicationBuilder UseFinanceSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();

    public static IApplicationBuilder UseFinanceProductionHsts(
        this IApplicationBuilder app,
        IHostEnvironment environment)
    {
        if (SecurityHeadersPolicy.ShouldUseHsts(environment.EnvironmentName))
            app.UseHsts();

        return app;
    }
}
