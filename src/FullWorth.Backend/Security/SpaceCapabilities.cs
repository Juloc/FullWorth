using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Security;

public static class PermissionCapabilities
{
    public static readonly string[] All =
    [
        "transactions.read", "transactions.categorize", "transactions.write", "budgets.manage",
        "contracts.manage", "purchases.manage", "investments.manage", "banking.manage",
        "sharing.manage", "export.read", "audit.read"
    ];

    private static readonly HashSet<string> Editor = new(StringComparer.OrdinalIgnoreCase)
    {
        "transactions.read", "transactions.categorize", "transactions.write", "budgets.manage",
        "contracts.manage", "purchases.manage", "investments.manage", "export.read"
    };

    private static readonly HashSet<string> Viewer = new(StringComparer.OrdinalIgnoreCase)
    {
        "transactions.read"
    };

    public static bool IsKnown(string capability) =>
        All.Contains(capability, StringComparer.OrdinalIgnoreCase);

    public static bool TemplateAllows(string template, string capability) => template.ToLowerInvariant() switch
    {
        "owner" => IsKnown(capability),
        "editor" => Editor.Contains(capability),
        _ => Viewer.Contains(capability)
    };
}

/// <summary>
/// Darf dieser Benutzer das in diesem Space? Eine Frage, eine Antwort.
///
/// Das lag in Modules/Parity/PermissionsErgonomicsParityModule.cs, in einer Klasse namens
/// PermissionsErgonomicsParityEndpoints - also in einer Endpunktdatei, obwohl es mit Endpunkten nichts
/// zu tun hat. Vier Module riefen es von dort ab (Audit, Budgets, Categories, Contracts), und jedes
/// holte sich damit eine Kante nach Parity. Diese eine Methode war allein fuer VIER der dreizehn
/// Modulzyklen verantwortlich - mehr als jede andere einzelne Ursache.
///
/// Die Reihenfolge ist Absicht: unbekannte Faehigkeit -> nein; kein Mitglied -> nein; Eigentuemer ->
/// ja; ausdrueckliche Einzelfreigabe schlaegt die Vorlage; sonst entscheidet die Vorlage der Rolle.
/// </summary>
internal static class SpaceCapabilities
{
    public static async Task<bool> HasCapabilityAsync(
        FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, string capability, CancellationToken ct)
    {
        if (!PermissionCapabilities.IsKnown(capability)) return false;
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role is null) return false;
        if (role == "owner") return true;

        var connection = await RawSql.OpenAsync(db, ct);
        await using (var overrideCommand = RawSql.Command(connection, """
SELECT "IsAllowed" FROM "FinanceCapabilityGrants"
WHERE "FullWorthSpaceId"=@space AND "UserId"=@user AND "Capability"=@capability
""", ("@space", fullWorthSpaceId), ("@user", userId), ("@capability", capability)))
        {
            var value = await overrideCommand.ExecuteScalarAsync(ct);
            if (value is not null and not DBNull) return Convert.ToBoolean(value);
        }

        var template = await LoadTemplateAsync(db, fullWorthSpaceId, userId, ct);
        return PermissionCapabilities.TemplateAllows(template, capability);
    }

    public static async Task<Dictionary<string, bool>> EffectiveCapabilitiesAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid userId, string template, CancellationToken ct)
    {
        if (string.Equals(template, "owner", StringComparison.OrdinalIgnoreCase))
            return PermissionCapabilities.All.ToDictionary(capability => capability, _ => true,
                StringComparer.OrdinalIgnoreCase);

        var overrides = await LoadOverridesAsync(db, fullWorthSpaceId, userId, ct);
        return PermissionCapabilities.All.ToDictionary(
            capability => capability,
            capability => overrides.TryGetValue(capability, out var value)
                ? value
                : PermissionCapabilities.TemplateAllows(template, capability),
            StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<string> LoadTemplateAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role == "owner") return "owner";
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Template" FROM "FinanceMemberRoleTemplates" WHERE "FullWorthSpaceId"=@space AND "UserId"=@user
""", ("@space", fullWorthSpaceId), ("@user", userId));
        return Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? "viewer";
    }

    private static async Task<Dictionary<string, bool>> LoadOverridesAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Capability","IsAllowed" FROM "FinanceCapabilityGrants" WHERE "FullWorthSpaceId"=@space AND "UserId"=@user
""", ("@space", fullWorthSpaceId), ("@user", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[RawSql.String(reader, "Capability")] = RawSql.Bool(reader, "IsAllowed");
        return result;
    }
}
