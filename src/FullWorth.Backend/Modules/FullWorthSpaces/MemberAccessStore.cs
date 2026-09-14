using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

/// <summary>Ein Mitglied, wie es in der Freigabeverwaltung erscheint.</summary>
public sealed record SpaceMemberRow(Guid UserId, string Role, string Email, string DisplayName);

/// <summary>
/// Die Mitglieder eines Space und ihre gespeicherten Rollenvorlagen und Einzelfreigaben.
///
/// Wer was aendern darf, entscheidet dieser Store ausdruecklich NICHT - das bleibt in
/// AccessEndpoints, wo man es sieht. Hier steht nur, wie die Entscheidung in die Datenbank kommt:
/// in einer Transaktion, damit eine halb gesetzte Rolle nicht existieren kann.
/// </summary>
public sealed class MemberAccessStore(FullWorthDbContext db, AuditService audit)
{
    /// <summary>
    /// Nur abgebildete Eigenschaften auswaehlen: <c>FullWorthUser.Email</c> ist ein bequemer Alias
    /// und absichtlich <c>NotMapped</c> - in einer EF-Abfrage scheitert er zur Laufzeit an der
    /// Uebersetzung.
    /// </summary>
    public async Task<IReadOnlyList<SpaceMemberRow>> ListMembersAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId)
            .Join(db.Users.AsNoTracking(), member => member.UserId, user => user.Id,
                (member, user) => new SpaceMemberRow(member.UserId, member.Role, user.EmailNormalized, user.DisplayName))
            .OrderBy(row => row.DisplayName).ThenBy(row => row.Email)
            .ToListAsync(ct);

    /// <summary>Die Rolle dieses Mitglieds - oder nichts, wenn es keines ist.</summary>
    public Task<string?> RoleOfAsync(Guid fullWorthSpaceId, Guid memberUserId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == memberUserId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// Setzt Vorlage und Abweichungen eines Mitglieds.
    ///
    /// Bei einem Eigentuemer werden Vorlage und Freigaben stattdessen geloescht: Eigentum ist
    /// strukturell privilegiert, und eine spaetere Herabstufung soll keine alten Einstellungen erben.
    ///
    /// Gespeichert wird nur, was von der Vorlage abweicht. Sonst friert man eine vollstaendige Kopie
    /// der Faehigkeitsmatrix ein, und eine spaetere Aenderung der Vorlage wirkt nicht mehr.
    /// </summary>
    public async Task SaveAccessAsync(
        Guid callerUserId, Guid fullWorthSpaceId, Guid memberUserId, string template,
        IReadOnlyDictionary<string, bool> overrides, bool targetIsOwner, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await RawSql.OpenAsync(db, ct);

        if (targetIsOwner)
        {
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

        audit.Record(fullWorthSpaceId, callerUserId, "sharing.access.updated", "FullWorthUser", memberUserId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
