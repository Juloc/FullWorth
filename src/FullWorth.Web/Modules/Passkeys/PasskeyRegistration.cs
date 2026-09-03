using FullWorth.Web.Data;
using Fido2NetLib;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Passkeys;

public static class PasskeyRegistration
{
    public static IServiceCollection AddPasskeys(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PasskeyOptions>(configuration.GetSection(PasskeyOptions.SectionName));

        services.AddSingleton<IFido2>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PasskeyOptions>>().Value;
            var environment = provider.GetRequiredService<IHostEnvironment>();
            options.Validate(environment.IsProduction());
            return new Fido2(new Fido2Configuration
            {
                ServerDomain = options.RelyingPartyId,
                ServerName = options.RelyingPartyName,
                Origins = options.Origins.ToHashSet(StringComparer.OrdinalIgnoreCase)
            });
        });

        services.AddScoped<IPasskeyStore>(provider =>
            new PasskeyStore(provider.GetRequiredService<AuthDbContext>()));
        services.AddScoped<IPasskeyChallengeStore>(provider =>
            new PasskeyChallengeStore(provider.GetRequiredService<AuthDbContext>()));
        services.AddScoped<IPasskeyUserLookup, PasskeyUserLookup>();
        services.AddScoped<PasskeySessionSignInService>();
        services.AddScoped<PasskeyService>();
        return services;
    }
}
