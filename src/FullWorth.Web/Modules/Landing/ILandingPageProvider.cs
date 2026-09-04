namespace FullWorth.Web.Modules.Landing;

/// <summary>
/// Extension point for the page an anonymous visitor sees at the public landing route. The public
/// self-hosted core registers <see cref="DefaultLandingPageProvider"/>. Private deployments (the
/// FullWorth Cloud marketing site, the public demo) may register their own provider to serve a
/// different landing — a demo landing, a hosted-signup page — <b>without</b> the public core ever
/// taking a dependency on <c>FullWorth.Cloud</c> or <c>FullWorth.Demo</c>. Only one provider is
/// active per deployment; the last registration wins.
/// </summary>
public interface ILandingPageProvider
{
    /// <summary>
    /// Produce the landing response for an anonymous visitor, given the current sanitized
    /// installation state. Implementations write directly to the response (redirect, HTML, etc.).
    /// </summary>
    Task RenderAsync(HttpContext context, InstallationState state, CancellationToken ct);
}
