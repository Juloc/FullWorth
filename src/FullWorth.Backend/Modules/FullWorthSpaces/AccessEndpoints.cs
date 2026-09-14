using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public sealed record MemberAccessWrite(string Template, IReadOnlyDictionary<string, bool>? Overrides);

/// <summary>Wer im Space was darf, und wie man es aendert.
///
/// Letzte Datei aus Parity: drei Fachbereiche in einer - Zugriff, Kategorie-Ergonomie und
/// Massenaenderungen an Buchungen. Die Berechtigungspruefung selbst ist schon vorher nach
/// Security/SpaceCapabilities gezogen; sie hielt allein vier Modulzyklen.
///
/// Die Entscheidungen stehen absichtlich hier und nicht im Store: wer wen wie hochstufen darf, ist
/// die Sicherheitsaussage dieser Datei, und sie gehoert an eine Stelle, an der man sie am Stueck
/// liest. Der Store weiss nur, wie das Ergebnis in die Datenbank kommt.</summary>
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
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var template = await space.TemplateAsync(userId, fullWorthSpaceId, ct);
        var capabilities = await space.EffectiveCapabilitiesAsync(userId, fullWorthSpaceId, template, ct);
        return Results.Ok(new { template, capabilities });
    }

    private static async Task<IResult> ListMembers(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space,
        MemberAccessStore store, CancellationToken ct)
    {
        var caller = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(caller, fullWorthSpaceId, "sharing.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var members = await store.ListMembersAsync(fullWorthSpaceId, ct);
        var result = new List<object>();
        foreach (var member in members)
        {
            var template = member.Role == "owner"
                ? "owner"
                : await space.TemplateAsync(member.UserId, fullWorthSpaceId, ct);
            result.Add(new
            {
                member.UserId,
                member.Email,
                member.DisplayName,
                template,
                capabilities = await space.EffectiveCapabilitiesAsync(member.UserId, fullWorthSpaceId, template, ct)
            });
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> PutMemberAccess(
        Guid memberUserId, Guid fullWorthSpaceId, MemberAccessWrite request, CurrentUserContext currentUser,
        SpaceAccess space, MemberAccessStore store, CancellationToken ct)
    {
        var caller = currentUser.RequireUserId();
        if (!await space.HasCapabilityAsync(caller, fullWorthSpaceId, "sharing.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var targetRole = await store.RoleOfAsync(fullWorthSpaceId, memberUserId, ct);
        if (targetRole is null) return Results.NotFound();

        var template = request.Template.Trim().ToLowerInvariant();
        if (template is not ("owner" or "editor" or "viewer"))
            return Results.BadRequest(new { error = "Template must be owner, editor or viewer." });

        var callerIsOwner = await space.IsOwnerAsync(caller, fullWorthSpaceId, ct);
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
            var callerTemplate = await space.TemplateAsync(caller, fullWorthSpaceId, ct);
            var callerCapabilities = await space.EffectiveCapabilitiesAsync(caller, fullWorthSpaceId, callerTemplate, ct);
            foreach (var capability in PermissionCapabilities.All)
            {
                var requested = overrides.TryGetValue(capability, out var explicitValue)
                    ? explicitValue
                    : PermissionCapabilities.TemplateAllows(template, capability);
                if (requested && !callerCapabilities.GetValueOrDefault(capability))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        await store.SaveAccessAsync(caller, fullWorthSpaceId, memberUserId, template, overrides, targetIsOwner, ct);
        return Results.NoContent();
    }
}
