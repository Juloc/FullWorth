using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Bootstrap;

public sealed record BootstrapAdminRequest(string Email, string DisplayName, string? SpaceName, string? BaseCurrency);
public sealed record BootstrapRegistrationRequest(string Email, string DisplayName, string? SpaceName, string? BaseCurrency);
public sealed record BootstrapUserLifecycleRequest(Guid FinanceUserId);
public sealed record BootstrapAdminResponse(Guid FinanceUserId, Guid FullWorthSpaceId);

public static class BootstrapEndpoints
{
    // Anything under this path is guarded by the internal key (see InternalUserContextMiddleware)
    // but deliberately requires no user context: it is the one-time first-run seam that creates the
    // very first user, so there is no authenticated actor yet.
    public const string BasePath = "/api/bootstrap";

    public static IEndpointRouteBuilder MapBootstrapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost($"{BasePath}/first-admin", async (
            BootstrapAdminRequest request,
            FullWorthDbContext db,
            UserStore users,
            FullWorthSpaceService spaces,
            IntelligenceAdminBootstrapper intelligenceAdminBootstrapper,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.DisplayName))
                return Results.BadRequest(new { error = "email and displayName are required." });

            // Bootstrap only runs on an empty system. Once any user exists it is a no-op, so this
            // endpoint can never be used to mint additional admins later.
            if (await db.Set<FullWorthUser>().AnyAsync(ct))
                return Results.Conflict(new { error = "The system is already bootstrapped." });

            FullWorthUser user;
            try
            {
                user = await users.CreateAsync(new CreateUserRequest(request.Email, request.DisplayName), ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var spaceName = string.IsNullOrWhiteSpace(request.SpaceName) ? "Household" : request.SpaceName.Trim();
            var space = await spaces.CreateAsync(user.Id, spaceName, request.BaseCurrency, ct);
            await intelligenceAdminBootstrapper.EnsureBootstrapAdminAsync(ct);

            return Results.Ok(new BootstrapAdminResponse(user.Id, space.Id));
        }).WithTags("Bootstrap");

        app.MapPost($"{BasePath}/register", async (
            BootstrapRegistrationRequest request,
            FullWorthDbContext db,
            UserStore users,
            FullWorthSpaceService spaces,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.DisplayName))
                return Results.BadRequest(new { error = "email and displayName are required." });

            try
            {
                var user = await users.CreateAsync(new CreateUserRequest(request.Email, request.DisplayName), ct);
                var spaceName = string.IsNullOrWhiteSpace(request.SpaceName) ? "Household" : request.SpaceName.Trim();
                var space = await spaces.CreateAsync(user.Id, spaceName, request.BaseCurrency, ct);
                return Results.Ok(new BootstrapAdminResponse(user.Id, space.Id));
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "registration_unavailable" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Bootstrap");

        app.MapPost($"{BasePath}/deactivate-user", async (
            BootstrapUserLifecycleRequest request,
            UserStore users,
            CancellationToken ct) =>
        {
            if (request.FinanceUserId == Guid.Empty) return Results.BadRequest();
            return await users.SetActiveAsync(request.FinanceUserId, false, ct)
                ? Results.NoContent()
                : Results.NotFound();
        }).WithTags("Bootstrap");

        app.MapPost($"{BasePath}/reactivate-user", async (
            BootstrapUserLifecycleRequest request,
            UserStore users,
            CancellationToken ct) =>
        {
            if (request.FinanceUserId == Guid.Empty) return Results.BadRequest();
            return await users.SetActiveAsync(request.FinanceUserId, true, ct)
                ? Results.NoContent()
                : Results.NotFound();
        }).WithTags("Bootstrap");

        app.MapPost($"{BasePath}/purge-user", async (
            BootstrapUserLifecycleRequest request,
            AccountPurgeService purge,
            CancellationToken ct) =>
        {
            if (request.FinanceUserId == Guid.Empty) return Results.BadRequest();
            var result = await purge.PurgeAsync(request.FinanceUserId, ct);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Account purge could not complete safely.",
                    extensions: new Dictionary<string, object?> { ["error"] = result.Error });
        }).WithTags("Bootstrap");

        // Claim an owner-issued invite (multi-user sharing). Lives under /api/bootstrap so it runs on the
        // internal key alone with NO user context (the invitee has no identity yet) — only the Web tier,
        // which holds the internal key, can call it. It creates/reuses the invitee's FullWorthUser, their
        // membership, and the requested account grants; the Web tier then creates the login + signs in.
        app.MapPost($"{BasePath}/accept-invite", async (
            AcceptInviteRequest request,
            FullWorthSpaceInviteStore invites,
            CancellationToken ct) =>
        {
            var result = await invites.AcceptAsync(request?.Token, ct);
            return result.Ok
                ? Results.Ok(new AcceptInviteResponse(result.FinanceUserId, result.Email))
                : Results.BadRequest(new { error = "invalid_invite" });
        }).WithTags("Bootstrap");

        return app;
    }
}
