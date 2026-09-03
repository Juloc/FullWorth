namespace FullWorth.Web.Security.Antiforgery;

public static class FullWorthAntiforgeryDefaults
{
    public const string HeaderName = "X-CSRF-TOKEN";
    public const string CookieName = "Finance.Antiforgery";
    public const string TokenEndpointPath = "/auth/antiforgery";
    public const string InvalidTokenMessage = "Invalid antiforgery token.";
}
