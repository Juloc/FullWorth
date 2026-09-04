namespace FullWorth.Web.Modules.Landing;

public static class LandingEndpoints
{
    public static IEndpointRouteBuilder MapLandingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Sanitized installation state (§8). Anonymous by design: a landing/setup page must be able to
        // read it before anyone is signed in. Emits no user counts or identities.
        endpoints.MapGet("/api/installation-state",
            async (InstallationStateService state, CancellationToken ct) =>
                Results.Ok((await state.GetAsync(ct)).ToSanitizedPayload()))
            .AllowAnonymous();

        // Public landing route (§7). Delegates to the registered ILandingPageProvider so a private
        // deployment can swap the landing without the public core depending on it. The default
        // provider redirects to the sign-in shell.
        endpoints.MapGet("/welcome",
            async (HttpContext context, ILandingPageProvider provider, InstallationStateService state, CancellationToken ct) =>
            {
                await provider.RenderAsync(context, await state.GetAsync(ct), ct);
            })
            .AllowAnonymous();

        return endpoints;
    }
}
