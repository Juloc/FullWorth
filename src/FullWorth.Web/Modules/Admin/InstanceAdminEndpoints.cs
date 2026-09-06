using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Admin;

public static class InstanceAdminEndpoints
{
    public static IEndpointRouteBuilder MapInstanceAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/capabilities", async (
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
            Results.Ok(await admin.GetCapabilitiesAsync(context.User, ct)))
            .RequireAuthorization();

        var group = endpoints.MapGroup("/auth/admin").RequireAuthorization();

        group.MapGet("/overview", async (
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            return actor is null
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Ok(await admin.GetOverviewAsync(ct));
        });

        group.MapGet("/users", async (
            HttpContext context,
            InstanceAdminService admin,
            string? search,
            string? status,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            return actor is null
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Ok(await admin.ListUsersAsync(search, status, offset ?? 0, limit ?? 50, ct));
        });

        group.MapGet("/users/{id:guid}", async (
            Guid id,
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var user = await admin.GetUserAsync(id, ct);
            return user is null ? Results.NotFound() : Results.Ok(user);
        });

        group.MapPost("/users/{id:guid}/disable", (
            Guid id,
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
            MutateAsync(context, admin, (actor, token) => admin.DisableAsync(actor, id, token), ct));

        group.MapPost("/users/{id:guid}/enable", (
            Guid id,
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
            MutateAsync(context, admin, (actor, token) => admin.EnableAsync(actor, id, token), ct));

        group.MapPost("/users/{id:guid}/revoke-sessions", (
            Guid id,
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
            MutateAsync(context, admin, (actor, token) => admin.RevokeSessionsAsync(actor, id, token), ct));

        group.MapPost("/users/{id:guid}/schedule-deletion", (
            Guid id,
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
            MutateAsync(context, admin, (actor, token) => admin.ScheduleDeletionAsync(actor, id, token), ct));

        group.MapPost("/users/{id:guid}/cancel-deletion", (
            Guid id,
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
            MutateAsync(context, admin, (actor, token) => admin.CancelDeletionAsync(actor, id, token), ct));

        group.MapPost("/users/{id:guid}/grant-admin", (
            Guid id,
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
            MutateAsync(context, admin, (actor, token) => admin.GrantAdminAsync(actor, id, token), ct));

        group.MapPost("/users/{id:guid}/revoke-admin", (
            Guid id,
            HttpContext context,
            InstanceAdminService admin,
            CancellationToken ct) =>
            MutateAsync(context, admin, (actor, token) => admin.RevokeAdminAsync(actor, id, token), ct));

        group.MapGet("/audit", async (
            HttpContext context,
            InstanceAdminService admin,
            int? limit,
            CancellationToken ct) =>
        {
            var actor = await admin.GetCurrentAdminAsync(context.User, ct);
            return actor is null
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Ok(await admin.ListAuditAsync(limit ?? 50, ct));
        });

        return endpoints;
    }

    private static async Task<IResult> MutateAsync(
        HttpContext context,
        InstanceAdminService admin,
        Func<Guid, CancellationToken, Task<AdminMutationResult>> action,
        CancellationToken ct)
    {
        var actor = await admin.GetCurrentAdminAsync(context.User, ct);
        if (actor is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var result = await action(actor.Id, ct);
        if (result.Succeeded)
            return Results.NoContent();

        return result.Error switch
        {
            "not_found" => Results.NotFound(),
            "last_admin" => Results.Conflict(new { error = "last_admin" }),
            "pending_deletion" or "invalid_state" or "deletion_deadline_passed" or "purge_in_progress" =>
                Results.Conflict(new { error = result.Error }),
            _ => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Admin action could not be completed.",
                extensions: new Dictionary<string, object?> { ["error"] = result.Error })
        };
    }
}
