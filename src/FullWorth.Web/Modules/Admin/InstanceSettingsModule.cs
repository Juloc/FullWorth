using FullWorth.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// Settings that describe the installation itself rather than any person in it.
///
/// Today that is the public address. It was the last line a deployment had to edit by hand, and it is
/// not a credential or an id — it is "which address am I reached at", which the app can simply learn:
/// the first registration happens on the real domain, through the real reverse proxy, with a human
/// present.
/// </summary>
public sealed class InstanceSettings
{
    /// <summary>There is exactly one row. The scope key makes that explicit and unique-indexable.</summary>
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = InstanceScopeKey;

    /// <summary>Origin only — scheme, host and port, never a path.</summary>
    public string PublicUrl { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class InstanceSettingsStore(AuthDbContext db)
{
    public async Task<string?> GetPublicUrlAsync(CancellationToken ct)
    {
        var row = await db.Set<InstanceSettings>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == InstanceSettings.InstanceScopeKey, ct);
        return string.IsNullOrWhiteSpace(row?.PublicUrl) ? null : row.PublicUrl;
    }

    /// <summary>
    /// Records the address, once. Create-only on purpose: the passkey relying party id is derived from
    /// it, and changing a relying party id makes every passkey already registered against it unusable.
    /// Returns the address in force afterwards, which is the existing one when there already was one.
    /// </summary>
    public async Task<string> RememberPublicUrlAsync(string origin, CancellationToken ct)
    {
        var row = await db.Set<InstanceSettings>()
            .SingleOrDefaultAsync(x => x.ScopeKey == InstanceSettings.InstanceScopeKey, ct);
        if (!string.IsNullOrWhiteSpace(row?.PublicUrl)) return row.PublicUrl;

        if (row is null)
        {
            row = new InstanceSettings { ScopeKey = InstanceSettings.InstanceScopeKey };
            db.Add(row);
        }

        row.PublicUrl = origin;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return row.PublicUrl;
    }
}
