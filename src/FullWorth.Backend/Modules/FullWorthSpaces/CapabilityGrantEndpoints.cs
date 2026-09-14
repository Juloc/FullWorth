using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public sealed record CapabilityGrantWrite(Guid UserId, string Capability, bool IsAllowed);

/// <summary>Einzelfreigaben je Mitglied. Kam aus Parity/ExperienceParityModule.</summary>
public static class CapabilityGrantEndpoints
{
    private static readonly HashSet<string> Capabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "view", "categorize", "edit_transactions", "edit_budgets", "edit_contracts",
        "manage_banking", "manage_imports", "manage_investments", "admin"
    };


    public static IEndpointRouteBuilder MapCapabilityGrantEndpoints(this IEndpointRouteBuilder app)
    {
        var grants = app.MapGroup("/api/capability-grants").WithTags("Sharing");
        grants.MapGet("/", ListGrants);
        grants.MapPut("/", PutGrant);
        return app;
    }

    private static async Task<IResult> ListGrants(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT "UserId","Capability","IsAllowed","UpdatedAt"
FROM "FinanceCapabilityGrants"
WHERE "FullWorthSpaceId"=@space
ORDER BY "UserId","Capability"
""", ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            userId = RawSql.Guid(reader, "UserId"),
            capability = RawSql.String(reader, "Capability"),
            isAllowed = RawSql.Bool(reader, "IsAllowed"),
            updatedAt = RawSql.Timestamp(reader, "UpdatedAt")
        });
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutGrant(
        Guid fullWorthSpaceId, CapabilityGrantWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        var capability = request.Capability.Trim().ToLowerInvariant();
        if (!Capabilities.Contains(capability) || !await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
                member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == request.UserId, ct))
            return Results.BadRequest();

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES (@space,@user,@capability,@allowed,@now)
ON CONFLICT ("FullWorthSpaceId","UserId","Capability") DO UPDATE SET
  "IsAllowed"=EXCLUDED."IsAllowed",
  "UpdatedAt"=EXCLUDED."UpdatedAt"
""",
            ("@space", fullWorthSpaceId), ("@user", request.UserId), ("@capability", capability),
            ("@allowed", request.IsAllowed), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "sharing.capability.updated", "FullWorthUser", request.UserId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

}
