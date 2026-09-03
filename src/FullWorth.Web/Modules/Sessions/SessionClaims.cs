using System.Security.Claims;

namespace FullWorth.Web.Modules.Sessions;

public static class SessionClaims
{
    public const string SessionId = "session_id";

    public static Claim CreateSessionIdClaim(Guid sessionId) =>
        new(SessionId, sessionId.ToString("D"));

    public static bool TryGetSessionId(ClaimsPrincipal principal, out Guid sessionId) =>
        Guid.TryParse(principal.FindFirstValue(SessionId), out sessionId);
}
