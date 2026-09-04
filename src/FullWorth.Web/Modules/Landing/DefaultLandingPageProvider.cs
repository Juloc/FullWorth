namespace FullWorth.Web.Modules.Landing;

/// <summary>
/// The landing a normal self-hosted FullWorth instance shows to an anonymous visitor: send them to
/// the sign-in shell. There is no marketing/demo content in the public core — a private deployment
/// that wants one registers its own <see cref="ILandingPageProvider"/>.
/// </summary>
public sealed class DefaultLandingPageProvider : ILandingPageProvider
{
    public Task RenderAsync(HttpContext context, InstallationState state, CancellationToken ct)
    {
        context.Response.Redirect("/auth/login");
        return Task.CompletedTask;
    }
}
