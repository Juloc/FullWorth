using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>
/// Keeps the legacy JSON snapshot endpoint compatible while enforcing the same explicit export.read
/// capability as the portable XLSX export. Account-level filtering remains inside ExportService.
/// </summary>
public sealed class ExportAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUserContext currentUser, FullWorthDbContext db)
    {
        if (HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.Equals("/api/export/snapshot", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var fullWorthSpaceId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var userId = currentUser.RequireUserId();
            // Non-members must not learn that the FullWorth Space exists: answer with the same 404 the
            // resource endpoints use, and reserve 403 for members who are missing the export capability.
            if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(
                    db, userId, fullWorthSpaceId, "export.read", context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await next(context);
    }
}
