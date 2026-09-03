using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>
/// Capability bridge for core/early endpoints that predate the owner/editor/viewer capability model.
/// It does not replace endpoint validation; it prevents legacy and newly integrated mutation routes
/// from bypassing the newer role policy before their existing handlers run.
/// </summary>
public sealed class LegacyParityCapabilityAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUserContext currentUser, FullWorthDbContext db)
    {
        var capability = RequiredCapability(context.Request.Method, context.Request.Path.Value ?? string.Empty);
        if (capability is null)
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var fullWorthSpaceId))
        {
            await next(context);
            return;
        }

        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(
                db, userId, fullWorthSpaceId, capability, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static string? RequiredCapability(string method, string path)
    {
        if (HttpMethods.IsPut(method) &&
            IsGuidRoute(path, "/api/account-experience/", "/appearance"))
            return "banking.manage";

        if (HttpMethods.IsPost(method) &&
            path.Equals("/api/import-jobs/upload", StringComparison.OrdinalIgnoreCase))
            return "transactions.write";

        if (HttpMethods.IsPost(method) &&
            (IsGuidRoute(path, "/api/import-jobs/", "/commit") ||
             IsGuidRoute(path, "/api/import-jobs/", "/cancel")))
            return "transactions.write";

        // Main's receipt queue, Amazon connection/sync and the older purchase mutation endpoints all
        // live below /api/purchases. They may still enforce membership/ownership themselves, but a
        // viewer must never reach a write handler merely because they can read that FullWorth Space.
        if (!HttpMethods.IsGet(method) &&
            !HttpMethods.IsHead(method) &&
            !HttpMethods.IsOptions(method) &&
            (path.Equals("/api/purchases", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/purchases/", StringComparison.OrdinalIgnoreCase)))
            return "purchases.manage";

        return null;
    }

    private static bool IsGuidRoute(string path, string prefix, string suffix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var value = path[prefix.Length..^suffix.Length].Trim('/');
        return Guid.TryParse(value, out _);
    }
}
