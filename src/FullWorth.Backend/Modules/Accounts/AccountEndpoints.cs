using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Accounts;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        group.MapGet("/", async (Guid? fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
            Results.Ok(await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct)));

        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var item = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPost("/", async (AccountCreateRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                var item = await store.CreateForMemberAsync(currentUser.RequireUserId(), request, ct);
                return item is null
                    ? Results.NotFound()
                    : Results.Created($"/api/accounts/{item.Id}?fullWorthSpaceId={item.FullWorthSpaceId}", item);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPut("/{id:guid}/balance", async (Guid id, Guid fullWorthSpaceId, ManualBalanceRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.SetManualBalanceAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct) switch
                {
                    ManualBalanceResult.Ok => Results.NoContent(),
                    ManualBalanceResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                    ManualBalanceResult.NotManual => Results.Conflict(new { error = "Balances of synced accounts are managed by their bank connection." }),
                    _ => Results.NotFound()
                };
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // The explicit, reversible "these two accounts are the same" link (O-4/O-5). Owner-gated and
        // ordered like the balance PUT: not-found → forbidden → conflict. No IBAN anywhere in sight, so
        // it works for PayPal, Wise, Revolut, cash and manual accounts too.
        group.MapGet("/{id:guid}/link", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var state = await store.GetLinkStateAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return state is null ? Results.NotFound() : Results.Ok(state);
        });

        group.MapPut("/{id:guid}/link", async (Guid id, Guid fullWorthSpaceId, AccountLinkRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.LinkAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request.DuplicateOfAccountId, ct) switch
                {
                    AccountLinkResult.Ok => Results.NoContent(),
                    AccountLinkResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                    AccountLinkResult.TargetIsLinked => Results.Conflict(new { error = "The chosen account is itself linked to another one. Link to that one instead." }),
                    AccountLinkResult.IsLinkTarget => Results.Conflict(new { error = "Other accounts are already counted as this one. Remove those links first." }),
                    AccountLinkResult.TargetNotCounted => Results.Conflict(new { error = "The chosen account is excluded from net worth, so linking would count the money nowhere." }),
                    _ => Results.NotFound()
                };
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/{id:guid}/link", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
            await store.UnlinkAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct) switch
            {
                AccountLinkResult.Ok => Results.NoContent(),
                AccountLinkResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                AccountLinkResult.NotLinked => Results.Conflict(new { error = "This account is not linked to another one." }),
                _ => Results.NotFound()
            });

        // Assign (or clear, groupId=null) an account's group. Dedicated endpoint — PATCH's
        // null-means-unchanged settings semantics can't express "ungroup". Owner-gated like the balance PUT.
        group.MapPut("/{id:guid}/group", async (Guid id, Guid fullWorthSpaceId, AccountGroupAssignRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
            await store.AssignAccountToGroupAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request.GroupId, ct) switch
            {
                AccountGroupResult.Ok => Results.NoContent(),
                AccountGroupResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            });

        group.MapPatch("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, AccountSettingsRequest request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (await store.GetForUserAsync(userId, fullWorthSpaceId, id, ct) is null) return Results.NotFound();
            if (!await store.HasEditAccessAsync(userId, fullWorthSpaceId, id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                return await store.UpdateSettingsForOwnerAsync(userId, fullWorthSpaceId, id, request, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (await store.GetForUserAsync(userId, fullWorthSpaceId, id, ct) is null) return Results.NotFound();
            if (!await store.HasEditAccessAsync(userId, fullWorthSpaceId, id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await store.ArchiveForOwnerAsync(userId, fullWorthSpaceId, id, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        group.MapGet("/{id:guid}/owners", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountService service, CancellationToken ct) =>
        {
            var owners = await service.ListOwnersAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return owners is null ? Results.NotFound() : Results.Ok(owners);
        });

        group.MapPost("/{id:guid}/owners", async (Guid id, Guid fullWorthSpaceId, AddAccountOwnerRequest request, CurrentUserContext currentUser, AccountService service, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await service.CanUserAccessAsync(userId, fullWorthSpaceId, id, ct)) return Results.NotFound();
            if (!await service.CanUserEditAsync(userId, fullWorthSpaceId, id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = await service.AddOwnerAsync(userId, fullWorthSpaceId, id, request.UserId, request.OwnershipType, ct);
            return result switch
            {
                AccountOwnerChangeResult.Added => Results.NoContent(),
                AccountOwnerChangeResult.TargetNotFullWorthSpaceMember or AccountOwnerChangeResult.NotFound => Results.NotFound(),
                AccountOwnerChangeResult.InvalidOwnershipType => Results.BadRequest(new { error = "Ownership type must be owner or viewer." }),
                AccountOwnerChangeResult.Duplicate => Results.Conflict(new { error = "The user already has account access." }),
                AccountOwnerChangeResult.AccessDenied => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.StatusCode(StatusCodes.Status409Conflict)
            };
        });

        group.MapDelete("/{id:guid}/owners/{targetUserId:guid}", async (Guid id, Guid targetUserId, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountService service, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await service.CanUserAccessAsync(userId, fullWorthSpaceId, id, ct)) return Results.NotFound();
            if (!await service.CanUserEditAsync(userId, fullWorthSpaceId, id, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = await service.RemoveOwnerAsync(userId, fullWorthSpaceId, id, targetUserId, ct);
            return result switch
            {
                AccountOwnerChangeResult.Removed => Results.NoContent(),
                AccountOwnerChangeResult.NotFound => Results.NotFound(),
                AccountOwnerChangeResult.LastOwner => Results.Conflict(new { error = "The last account owner cannot be removed." }),
                AccountOwnerChangeResult.AccessDenied => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.StatusCode(StatusCodes.Status409Conflict)
            };
        });

        return app;
    }
}

public static class AccountGroupEndpoints
{
    public static IEndpointRouteBuilder MapAccountGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account-groups").WithTags("Accounts");

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            var result = await store.ListGroupsForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result.Found ? Results.Ok(result.Groups) : Results.NotFound();
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, AccountGroupWrite request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                var dto = await store.CreateGroupForMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return dto is null ? Results.NotFound() : Results.Created($"/api/account-groups/{dto.Id}?fullWorthSpaceId={dto.FullWorthSpaceId}", dto);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapPut("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, AccountGroupWrite request, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.RenameGroupForMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, AccountStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.DeleteGroupForMemberAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)
                    ? Results.NoContent() : Results.NotFound();
            }
            // Die Standardgruppe laesst sich nicht loeschen - das ist eine Regel, kein fehlender Datensatz.
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        return app;
    }
}
