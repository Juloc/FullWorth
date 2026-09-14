using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

/// <summary>Eine Einzelfreigabe, wie sie in <c>FinanceCapabilityGrants</c> steht.</summary>
public sealed record CapabilityGrantRow(Guid UserId, string Capability, bool IsAllowed, DateTimeOffset UpdatedAt);

/// <summary>
/// Die ausdruecklichen Ja/Nein je Mitglied und Faehigkeit - die Schicht, die die Rollenvorlage
/// ueberstimmt. Wer sie lesen und setzen darf, entscheidet der Aufrufer; das ist eine andere Frage.
/// </summary>
public sealed class CapabilityGrantStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<IReadOnlyList<CapabilityGrantRow>> ListAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT "UserId","Capability","IsAllowed","UpdatedAt"
FROM "FinanceCapabilityGrants"
WHERE "FullWorthSpaceId"=@space
ORDER BY "UserId","Capability"
""", ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<CapabilityGrantRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new CapabilityGrantRow(
                RawSql.Guid(reader, "UserId"),
                RawSql.String(reader, "Capability"),
                RawSql.Bool(reader, "IsAllowed"),
                RawSql.Timestamp(reader, "UpdatedAt")));
        return rows;
    }

    /// <summary>Ist der Begünstigte ueberhaupt Mitglied? Eine Freigabe fuer einen Fremden ergibt keinen Sinn.</summary>
    public Task<bool> IsMemberAsync(Guid fullWorthSpaceId, Guid memberUserId, CancellationToken ct) =>
        RawSql.IsMemberAsync(db, memberUserId, fullWorthSpaceId, ct);

    public async Task SetAsync(
        Guid actorUserId, Guid fullWorthSpaceId, Guid memberUserId, string capability, bool isAllowed,
        CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES (@space,@user,@capability,@allowed,@now)
ON CONFLICT ("FullWorthSpaceId","UserId","Capability") DO UPDATE SET
  "IsAllowed"=EXCLUDED."IsAllowed",
  "UpdatedAt"=EXCLUDED."UpdatedAt"
""",
            ("@space", fullWorthSpaceId), ("@user", memberUserId), ("@capability", capability),
            ("@allowed", isAllowed), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, actorUserId, "sharing.capability.updated", "FullWorthUser", memberUserId);
        await db.SaveChangesAsync(ct);
    }
}
