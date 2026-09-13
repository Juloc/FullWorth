using FullWorth.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// One stored setting. Instance-wide: there is no per-user row here, and no owner column, because
/// everything in the catalogue describes the installation rather than a person in it.
/// </summary>
public sealed class InstanceConfigurationValue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    /// <summary>Whether <see cref="Value"/> is data-protection ciphertext rather than the value.</summary>
    public bool IsProtected { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>What the admin surface reports about one setting.</summary>
/// <param name="Source">
/// Which layer actually won: <c>environment</c>, <c>stored</c> or <c>default</c>. This is the one
/// thing none of the existing settings surfaces in this app report, and its absence is a trap: an
/// administrator edits a field, an environment variable silently overrides it, and the application
/// looks broken. A field the environment has pinned is shown read-only and says so.
/// </param>
public sealed record InstanceSettingView(
    string Key,
    string Section,
    string Kind,
    string? Value,
    bool Stored,
    string Source,
    bool ReadOnly,
    int? Minimum,
    int? Maximum,
    IReadOnlyList<string>? Choices);

public sealed class InstanceConfigurationStore(AuthDbContext db, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector =
        protection.CreateProtector("FullWorth.Web.InstanceConfiguration.v1");

    /// <summary>Every stored value, decrypted, ready to be published into configuration.</summary>
    public async Task<Dictionary<string, string?>> ReadAllAsync(CancellationToken ct)
    {
        var rows = await db.Set<InstanceConfigurationValue>().AsNoTracking().ToListAsync(ct);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // Only keys the catalogue still knows. A key removed from the catalogue stops being
            // published rather than lingering as configuration nobody can see or edit.
            if (InstanceSettingCatalogue.Find(row.Key) is null) continue;
            if (Reveal(row) is { } value) values[row.Key] = value;
        }
        return values;
    }

    public async Task<IReadOnlyList<InstanceConfigurationValue>> ListAsync(CancellationToken ct) =>
        await db.Set<InstanceConfigurationValue>().AsNoTracking().ToListAsync(ct);

    /// <summary>
    /// Writes one setting, or removes it when the value is blank. Removing is how an administrator
    /// hands a key back to the appsettings default — there is no third state to explain.
    /// </summary>
    public async Task SetAsync(string key, string? value, Guid? actor, CancellationToken ct)
    {
        var descriptor = InstanceSettingCatalogue.Find(key)
            ?? throw new ArgumentException("setting_unknown");

        var row = await db.Set<InstanceConfigurationValue>().SingleOrDefaultAsync(x => x.Key == key, ct);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (row is not null) db.Remove(row);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (InstanceSettingCatalogue.Validate(descriptor, value) is { } problem)
            throw new ArgumentException(problem);

        var trimmed = value.Trim();
        var secret = descriptor.Kind == InstanceSettingKind.Secret;

        if (row is null)
        {
            row = new InstanceConfigurationValue { Key = descriptor.Key };
            db.Add(row);
        }

        row.Value = secret ? _protector.Protect(trimmed) : trimmed;
        row.IsProtected = secret;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedByUserId = actor;
        await db.SaveChangesAsync(ct);
    }

    private string? Reveal(InstanceConfigurationValue row)
    {
        if (!row.IsProtected) return row.Value;
        try
        {
            return _protector.Unprotect(row.Value);
        }
        catch (CryptographicException)
        {
            // A lost key ring degrades to "not configured" rather than to a 500. The same choice as
            // ExternalAuthSettingsStore, and for the same reason: the app must still start.
            return null;
        }
    }
}
