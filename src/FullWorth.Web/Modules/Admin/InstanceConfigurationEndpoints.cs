using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Admin;

public sealed record InstanceSettingWrite(string Key, string? Value);

/// <summary>
/// The admin surface for settings that describe the installation.
///
/// Admin-only, like the rest of <see cref="InstanceAdminEndpoints"/>: every value here changes how the
/// whole installation behaves, not one person's view of it.
///
/// The read reports, per setting, which layer actually won. That is the part none of the older
/// settings surfaces in this app has, and its absence is a trap — an administrator edits a field, an
/// environment variable silently overrides it, and the application looks broken.
/// </summary>
public static class InstanceConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapInstanceConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/admin/instance-settings").RequireAuthorization();

        group.MapGet("/", async (
            HttpContext context,
            InstanceAdminService admin,
            InstanceConfigurationService settings,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            return actor is null
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Ok(await settings.ListAsync(ct));
        });

        group.MapPut("/", async (
            InstanceSettingWrite request,
            HttpContext context,
            InstanceAdminService admin,
            InstanceConfigurationService settings,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                // Saves and republishes, so the value applies without a restart. A log level changed
                // here takes effect on the next line written - which is the moment you want it.
                await settings.SetAsync(request.Key, request.Value, actor.Id, ct);
            }
            catch (ArgumentException problem)
            {
                // The reason is a machine-readable token from the catalogue, never the value that was
                // rejected: a validation message is a response body, and a response body is a log line
                // somewhere.
                return Results.BadRequest(new { error = problem.Message });
            }

            return Results.Ok(await settings.ListAsync(ct));
        });

        return endpoints;
    }
}
