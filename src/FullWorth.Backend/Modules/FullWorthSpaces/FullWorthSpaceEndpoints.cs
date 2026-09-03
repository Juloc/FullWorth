using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public static class FullWorthSpaceEndpoints
{
    public static IEndpointRouteBuilder MapFullWorthSpaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/fullworth-spaces").WithTags("FullWorth Spaces");

        // The browser app needs to know the caller's space(s) to scope every other request; only
        // spaces the user is a member of are returned, so nothing leaks across tenants.
        group.MapGet("/", async (CurrentUserContext currentUser, FullWorthSpaceStore store, CancellationToken ct) =>
        {
            var spaces = await store.ListForUserAsync(currentUser.RequireUserId(), ct);
            return Results.Ok(spaces.Select(space =>
                new FullWorthSpaceDto(space.Id, space.Name, space.BaseCurrency, space.CreatedAt, space.UpdatedAt)));
        });

        // --- Members (multi-user sharing) ---

        // Any member may read the roster so the sharing UI can list co-members and populate share pickers.
        group.MapGet("/{spaceId:guid}/members", async (
            Guid spaceId, CurrentUserContext currentUser, FullWorthSpaceStore store, CancellationToken ct) =>
        {
            var members = await store.ListMembersForUserAsync(currentUser.RequireUserId(), spaceId, ct);
            return members is null ? Results.NotFound() : Results.Ok(members);
        });

        // Add an EXISTING user (by email) directly. Unknown email → 404 (no global existence leak).
        group.MapPost("/{spaceId:guid}/members", async (
            Guid spaceId, AddMemberByEmailRequest request, CurrentUserContext currentUser,
            FullWorthSpaceStore store, UserStore users, CancellationToken ct) =>
        {
            var actingUserId = currentUser.RequireUserId();
            if (!await store.IsMemberAsync(actingUserId, spaceId, ct)) return Results.NotFound();
            if (!await store.IsOwnerAsync(actingUserId, spaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(request.Email)) return Results.BadRequest(new { error = "email is required." });
            var role = FullWorthSpaceRoles.IsValid(request.Role) ? request.Role : FullWorthSpaceRoles.Member;

            var user = await users.GetByEmailAsync(request.Email, ct);
            if (user is null) return Results.NotFound(new { error = "unknown_user" });
            try
            {
                await store.AddMemberAsync(actingUserId, spaceId, user.Id, role, ct);
                return Results.Ok(new FullWorthSpaceMemberDto(spaceId, user.Id, role, DateTimeOffset.UtcNow));
            }
            catch (FullWorthSpaceMembershipExistsException) { return Results.Conflict(new { error = "already_member" }); }
            catch (FullWorthSpaceNotFoundException) { return Results.NotFound(); }
        });

        group.MapDelete("/{spaceId:guid}/members/{userId:guid}", async (
            Guid spaceId, Guid userId, CurrentUserContext currentUser, FullWorthSpaceStore store, CancellationToken ct) =>
        {
            try
            {
                await store.RemoveMemberAsync(currentUser.RequireUserId(), spaceId, userId, ct);
                return Results.NoContent();
            }
            catch (FullWorthSpaceLastOwnerException) { return Results.Conflict(new { error = "last_owner" }); }
            catch (FullWorthSpaceNotFoundException) { return Results.NotFound(); }
        });

        // --- Invites ---

        group.MapPost("/{spaceId:guid}/invites", async (
            Guid spaceId, CreateInviteRequest request, CurrentUserContext currentUser,
            FullWorthSpaceInviteStore invites, CancellationToken ct) =>
        {
            var result = await invites.CreateAsync(
                currentUser.RequireUserId(), spaceId, request.Email, request.Role, request.Accounts ?? [], ct);
            return result.Status switch
            {
                InviteCreateStatus.Ok => Results.Created(
                    $"/api/fullworth-spaces/{spaceId}/invites/{result.View!.Id}",
                    new CreateInviteResponse(result.View!.Id, result.View.Email, result.View.SpaceRole, result.View.ExpiresAt, result.ClaimToken!)),
                InviteCreateStatus.SpaceNotFound => Results.NotFound(),
                InviteCreateStatus.NotOwner => Results.StatusCode(StatusCodes.Status403Forbidden),
                InviteCreateStatus.InvalidEmail => Results.BadRequest(new { error = "invalid_email" }),
                InviteCreateStatus.InvalidGrant => Results.BadRequest(new { error = "invalid_grant" }),
                InviteCreateStatus.AlreadyMember => Results.Conflict(new { error = "already_member" }),
                InviteCreateStatus.OpenInviteExists => Results.Conflict(new { error = "open_invite_exists" }),
                _ => Results.BadRequest()
            };
        });

        group.MapGet("/{spaceId:guid}/invites", async (
            Guid spaceId, CurrentUserContext currentUser, FullWorthSpaceInviteStore invites, CancellationToken ct) =>
        {
            var (status, list) = await invites.ListAsync(currentUser.RequireUserId(), spaceId, ct);
            return status switch
            {
                InviteAdminStatus.Ok => Results.Ok(list),
                InviteAdminStatus.NotOwner => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        });

        group.MapDelete("/{spaceId:guid}/invites/{inviteId:guid}", async (
            Guid spaceId, Guid inviteId, CurrentUserContext currentUser, FullWorthSpaceInviteStore invites, CancellationToken ct) =>
        {
            var status = await invites.RevokeAsync(currentUser.RequireUserId(), spaceId, inviteId, ct);
            return status switch
            {
                InviteAdminStatus.Ok => Results.NoContent(),
                InviteAdminStatus.NotOwner => Results.StatusCode(StatusCodes.Status403Forbidden),
                InviteAdminStatus.AlreadyClaimed => Results.Conflict(new { error = "already_claimed" }),
                _ => Results.NotFound()
            };
        });

        return app;
    }
}
