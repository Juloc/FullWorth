using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// The admin surface for external sign-in providers.
///
/// Admin-only because it changes how everybody on this installation can log in — the same grant the rest
/// of <see cref="InstanceAdminEndpoints"/> uses.
///
/// Secrets travel one way. The read answers "a secret is stored", never the value: a credential that has
/// been saved has no reason to go back to a browser, and a form that round-trips it turns every page load
/// into another chance to leak it.
/// </summary>
public static class ExternalAuthSettingsEndpoints
{
    public static IEndpointRouteBuilder MapExternalAuthSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/admin/external-providers").RequireAuthorization();

        group.MapGet("/", async (
            HttpContext context,
            InstanceAdminService admin,
            ExternalAuthSettingsStore store,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            return actor is null
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Ok(await store.GetViewAsync(ct));
        });

        group.MapPut("/", async (
            ExternalAuthSettingsWrite request,
            HttpContext context,
            InstanceAdminService admin,
            ExternalAuthSettingsStore store,
            ExternalAuthSchemeSynchronizer synchronizer,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var saved = await store.SetAsync(request, ct);

            // Adds the scheme when credentials arrive, removes it when they are cleared, and drops the
            // cached options either way. Without this a save would take effect only on the next
            // restart - and an admin would have every reason to think it did nothing.
            await synchronizer.SynchronizeAsync();

            return Results.Ok(saved);
        });

        return endpoints;
    }
}
