using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public sealed record MemberAccessWrite(string Template, IReadOnlyDictionary<string, bool>? Overrides);

/// <summary>Wer im Space was darf, und wie man es aendert.
///
/// Letzte Datei aus Parity: drei Fachbereiche in einer - Zugriff, Kategorie-Ergonomie und
/// Massenaenderungen an Buchungen. Die Berechtigungspruefung selbst ist schon vorher nach
/// Security/SpaceCapabilities gezogen; sie hielt allein vier Modulzyklen.</summary>
public static class AccessEndpoints
{
    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var access = app.MapGroup("/api/access").WithTags("Sharing");
        access.MapGet("/effective", GetEffectiveAccess);
        access.MapGet("/members", ListMembers);
        access.MapPut("/members/{memberUserId:guid}", PutMemberAccess);
        return app;
    }

    private static async Task<IResult> GetEffectiveAccess(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var template = await SpaceCapabilities.LoadTemplateAsync(db, fullWorthSpaceId, userId, ct);
        var capabilities = await SpaceCapabilities.EffectiveCapabilitiesAsync(db, fullWorthSpaceId, userId, template, ct);
        return Results.Ok(new { template, capabilities });
    }

    private static async Task<IResult> ListMembers(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var caller = currentUser.RequireUserId();
        if (!await SpaceCapabilities.HasCapabilityAsync(db, caller, fullWorthSpaceId, "sharing.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // Project only mapped properties. FullWorthUser.Email is a convenience alias and is intentionally
        // NotMapped, so using it inside an EF query would fail translation at runtime.
        var members = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId)
            .Join(db.Users.AsNoTracking(), member => member.UserId, user => user.Id,
                (member, user) => new
                {
                    member.UserId,
                    member.Role,
                    Email = user.EmailNormalized,
                    user.DisplayName
                })
            .OrderBy(row => row.DisplayName).ThenBy(row => row.Email)
            .ToListAsync(ct);

        var result = new List<object>();
        foreach (var member in members)
        {
            var template = member.Role == "owner"
                ? "owner"
                : await SpaceCapabilities.LoadTemplateAsync(db, fullWorthSpaceId, member.UserId, ct);
            result.Add(new
            {
                member.UserId,
                member.Email,
                member.DisplayName,
                template,
                capabilities = await SpaceCapabilities.EffectiveCapabilitiesAsync(db, fullWorthSpaceId, member.UserId, template, ct)
            });
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> PutMemberAccess(
        Guid memberUserId, Guid fullWorthSpaceId, MemberAccessWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var caller = currentUser.RequireUserId();
        if (!await SpaceCapabilities.HasCapabilityAsync(db, caller, fullWorthSpaceId, "sharing.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var targetRole = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == memberUserId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (targetRole is null) return Results.NotFound();

        var template = request.Template.Trim().ToLowerInvariant();
        if (template is not ("owner" or "editor" or "viewer"))
            return Results.BadRequest(new { error = "Template must be owner, editor or viewer." });

        var callerIsOwner = await RawSql.IsOwnerAsync(db, caller, fullWorthSpaceId, ct);
        var targetIsOwner = string.Equals(targetRole, "owner", StringComparison.OrdinalIgnoreCase);

        // Role templates refine ordinary members; they never manufacture a shadow FullWorth-Space owner.
        // Actual owner semantics remain in FullWorthSpaceMembers and therefore keep last-owner protections.
        if (targetIsOwner)
        {
            if (!callerIsOwner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (template != "owner")
                return Results.BadRequest(new { error = "FullWorth-Space owners always use the owner template." });
        }
        else if (template == "owner")
        {
            return Results.BadRequest(new { error = "Use the FullWorth-Space ownership flow to promote an owner." });
        }

        var overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Overrides ?? new Dictionary<string, bool>())
        {
            var key = pair.Key.Trim().ToLowerInvariant();
            if (!PermissionCapabilities.IsKnown(key))
                return Results.BadRequest(new { error = $"Unknown capability '{pair.Key}'." });
            overrides[key] = pair.Value;
        }

        // A delegated sharing manager may administer peers, but cannot grant a privilege that the
        // caller does not possess. This prevents privilege escalation through a second account.
        if (!callerIsOwner && !targetIsOwner)
        {
            var callerTemplate = await SpaceCapabilities.LoadTemplateAsync(db, fullWorthSpaceId, caller, ct);
            var callerCapabilities = await SpaceCapabilities.EffectiveCapabilitiesAsync(db, fullWorthSpaceId, caller, callerTemplate, ct);
            foreach (var capability in PermissionCapabilities.All)
            {
                var requested = overrides.TryGetValue(capability, out var explicitValue)
                    ? explicitValue
                    : PermissionCapabilities.TemplateAllows(template, capability);
                if (requested && !callerCapabilities.GetValueOrDefault(capability))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await RawSql.OpenAsync(db, ct);

        if (targetIsOwner)
        {
            // Owner is structurally privileged. Remove stale template/override rows so a future
            // demotion cannot unexpectedly inherit old settings.
            await using (var deleteTemplate = RawSql.Command(connection,
                             "DELETE FROM \"FinanceMemberRoleTemplates\" WHERE \"FullWorthSpaceId\"=@space AND \"UserId\"=@user",
                             ("@space", fullWorthSpaceId), ("@user", memberUserId)))
                await deleteTemplate.ExecuteNonQueryAsync(ct);
            await using (var deleteOverrides = RawSql.Command(connection,
                             "DELETE FROM \"FinanceCapabilityGrants\" WHERE \"FullWorthSpaceId\"=@space AND \"UserId\"=@user",
                             ("@space", fullWorthSpaceId), ("@user", memberUserId)))
                await deleteOverrides.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using (var templateCommand = RawSql.Command(connection, """
INSERT INTO "FinanceMemberRoleTemplates" ("FullWorthSpaceId","UserId","Template","UpdatedAt")
VALUES (@space,@user,@template,@now)
ON CONFLICT ("FullWorthSpaceId","UserId") DO UPDATE SET "Template"=EXCLUDED."Template","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@space", fullWorthSpaceId), ("@user", memberUserId), ("@template", template), ("@now", DateTimeOffset.UtcNow)))
            {
                await templateCommand.ExecuteNonQueryAsync(ct);
            }

            await using (var delete = RawSql.Command(connection,
                             "DELETE FROM \"FinanceCapabilityGrants\" WHERE \"FullWorthSpaceId\"=@space AND \"UserId\"=@user",
                             ("@space", fullWorthSpaceId), ("@user", memberUserId)))
                await delete.ExecuteNonQueryAsync(ct);

            // Persist only differences from the selected template. This keeps template changes
            // predictable instead of freezing a full copied capability matrix as overrides.
            foreach (var pair in overrides.Where(pair =>
                         pair.Value != PermissionCapabilities.TemplateAllows(template, pair.Key)))
            {
                await using var command = RawSql.Command(connection, """
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES (@space,@user,@capability,@allowed,@now)
""", ("@space", fullWorthSpaceId), ("@user", memberUserId),
                    ("@capability", pair.Key), ("@allowed", pair.Value), ("@now", DateTimeOffset.UtcNow));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        audit.Record(fullWorthSpaceId, caller, "sharing.access.updated", "FullWorthUser", memberUserId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

}
