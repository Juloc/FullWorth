using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Admin;

public sealed record VaultElevateRequest(string? Secret);
public sealed record VaultRevealRequest(string? Reference, string? Secret);

/// <summary>
/// The vault's HTTP surface.
///
/// Reveal is POST and not GET. A reference in a URL ends up in the access log, in <c>Referer</c> and
/// in the browser history, and <c>secure-fetch.js</c> attaches the CSRF token only to unsafe methods —
/// so GET would have been both leakier and less protected.
///
/// Authorisation falls exactly once, here, against <c>AuthUser.IsAdmin</c>. The finance backend has a
/// second notion of administrator (<c>IntelligenceAdminGrant</c>) that is bootstrapped onto the oldest
/// finance user and never follows a demotion in auth; using it here would mean a demoted administrator
/// keeps the vault forever.
/// </summary>
public static class AdminVaultEndpoints
{
    public static IEndpointRouteBuilder MapAdminVaultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/admin/vault").RequireAuthorization();

        group.MapGet("/", async (
            HttpContext context,
            InstanceAdminService admin,
            UserManager<Auth.AuthUser> users,
            AdminVaultService vault,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            if (actor is null) return Forbidden();
            if (!SessionClaims.TryGetSessionId(context.User, out var sessionId)) return Forbidden();

            NoStore(context);
            return Results.Ok(await vault.InventoryAsync(
                actor.Id, sessionId, await users.GetTwoFactorEnabledAsync(actor), ct));
        });

        group.MapPost("/elevate", async (
            VaultElevateRequest request,
            HttpContext context,
            InstanceAdminService admin,
            AdminElevationService elevations,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            if (actor is null) return Forbidden();
            if (!SessionClaims.TryGetSessionId(context.User, out var sessionId)) return Forbidden();

            NoStore(context);
            var result = await elevations.ElevateAsync(actor, sessionId, request.Secret, ct);
            return result.Granted
                ? Results.Ok(new
                {
                    elevatedUntil = result.Elevation!.ExpiresAt,
                    revealsLeft = AdminElevation.RevealBudget - result.Elevation.RevealsUsed
                })
                : Results.Json(
                    new { error = ErrorOf(result.Outcome) }, statusCode: StatusCodes.Status403Forbidden);
        });

        group.MapPost("/reveal", async (
            VaultRevealRequest request,
            HttpContext context,
            InstanceAdminService admin,
            AdminElevationService elevations,
            AdminVaultService vault,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            if (actor is null) return Forbidden();
            if (!SessionClaims.TryGetSessionId(context.User, out var sessionId)) return Forbidden();

            NoStore(context);

            var descriptor = AdminVaultCatalogue.Find(request.Reference);
            if (descriptor is null) return Results.BadRequest(new { error = "unknown_reference" });

            // The four that own the installation demand the factor for THIS reveal, not once for ten.
            // An open window is not enough for them; a proof is.
            if (descriptor.RequiresFreshFactor)
            {
                var fresh = await elevations.ElevateAsync(actor, sessionId, request.Secret, ct);
                if (!fresh.Granted)
                    return Results.Json(
                        new { error = ErrorOf(fresh.Outcome) }, statusCode: StatusCodes.Status403Forbidden);
            }

            var elevation = await elevations.CurrentAsync(actor.Id, sessionId, consume: true, ct);
            if (elevation is null)
                return Results.Json(
                    new { error = "elevation_required" }, statusCode: StatusCodes.Status403Forbidden);

            var revealed = await vault.RevealAsync(
                actor.Id, actor.Email ?? actor.Id.ToString(), request.Reference, ct);

            return revealed.Found
                ? Results.Ok(new
                {
                    reference = descriptor.Reference,
                    value = revealed.Value,
                    revealsLeft = AdminElevation.RevealBudget - elevation.RevealsUsed
                })
                : Results.Json(new { error = revealed.Error }, statusCode: StatusCodes.Status404NotFound);
        });

        return endpoints;
    }

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);

    /// <summary>
    /// No intermediary, no disk cache, no back-button replay. A cleartext secret that a proxy is free
    /// to store is not a secret any more.
    /// </summary>
    private static void NoStore(HttpContext context) =>
        context.Response.Headers.CacheControl = "no-store, max-age=0";

    private static string ErrorOf(ElevationOutcome outcome) => outcome switch
    {
        ElevationOutcome.LockedOut => "vault_locked",
        ElevationOutcome.CodeAlreadyUsed => "code_already_used",
        ElevationOutcome.NoSession => "session_gone",
        _ => "wrong_factor"
    };
}
