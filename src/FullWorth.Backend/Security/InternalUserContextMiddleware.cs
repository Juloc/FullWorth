using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Modules.Bootstrap;
using FullWorth.Backend.Modules.Users;

namespace FullWorth.Backend.Security;

public sealed class InternalUserContextMiddleware(
    RequestDelegate next,
    InternalUserContextOptions options)
{
    public async Task InvokeAsync(
        HttpContext context,
        UserStore users,
        CurrentUserContext currentUser)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        if (!TryGetSingleHeader(context, BackendContextHeaders.InternalKey, out var suppliedKey) ||
            !KeysMatch(suppliedKey, options.InternalKey))
        {
            Reject(context);
            return;
        }

        // First-run bootstrap has no authenticated user yet: it runs on a valid internal key alone.
        if (context.Request.Path.StartsWithSegments(BootstrapEndpoints.BasePath))
        {
            await next(context);
            return;
        }

        if (!TryGetSingleHeader(context, BackendContextHeaders.UserId, out var suppliedUserId) ||
            !Guid.TryParse(suppliedUserId, out var userId) ||
            userId == Guid.Empty)
        {
            Reject(context);
            return;
        }

        var user = await users.GetAsync(userId, context.RequestAborted);
        if (user is null || !user.IsActive)
        {
            Reject(context);
            return;
        }

        currentUser.SetAuthenticated(user.Id);
        await next(context);
    }

    private static bool TryGetSingleHeader(HttpContext context, string name, out string value)
    {
        var values = context.Request.Headers[name];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            value = string.Empty;
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static bool KeysMatch(string supplied, string configured)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);
        return suppliedBytes.Length == configuredBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }

    private static void Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}
