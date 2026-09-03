using Microsoft.AspNetCore.Http;

namespace FullWorth.Web.Security.RateLimiting;

public static class RateLimitPolicySelection
{
    private static readonly HashSet<string> PasskeyMutationPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/auth/passkeys/login/begin",
        "/auth/passkeys/login/complete",
        "/auth/passkeys/register/begin",
        "/auth/passkeys/register/complete"
    };

    public static string? ForRequest(string method, PathString path)
    {
        var value = path.Value ?? string.Empty;

        if (HttpMethods.IsPost(method))
        {
            if (string.Equals(value, "/auth/login", StringComparison.OrdinalIgnoreCase))
                return RateLimitPolicies.Login;

            if (string.Equals(value, "/auth/password-reset/request", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "/auth/password-reset/complete", StringComparison.OrdinalIgnoreCase))
                return RateLimitPolicies.PasswordReset;

            if (PasskeyMutationPaths.Contains(value))
                return RateLimitPolicies.Passkey;
        }

        if (path.StartsWithSegments("/bff/backend") || path.StartsWithSegments("/bff/banking"))
            return RateLimitPolicies.BrowserApi;

        return null;
    }
}
