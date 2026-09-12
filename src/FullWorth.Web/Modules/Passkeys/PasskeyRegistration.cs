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

        // Scoped, and built from the MONITOR rather than a snapshot.
        //
        // The relying party id comes from the installation's public address, which a fresh instance
        // learns from its first registration rather than from a compose file. As a singleton over
        // IOptions this was fixed for the life of the process, so a just-installed instance would have
        // kept the development default until somebody restarted it - and passkeys registered against
        // that default are worthless the moment the real one takes over.
        //
        // Validation moves with it: it is the request that needs a usable relying party, not startup,
        // and a brand-new instance has no address yet and nobody to use a passkey either.
        services.AddScoped<IFido2>(provider =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<PasskeyOptions>>().CurrentValue;
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
