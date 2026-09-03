using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Preferences;

/// <summary>
/// A per-user, per-FullWorth-Space JSON preference (UI_UX_SPEC §22). Dashboard layouts and similar UI
/// state live here so they are representable per user and per space, and so a future space switcher
/// only changes the scoping context, not the data model. The value is opaque UI JSON — no finance
/// truth is stored as a preference.
/// </summary>
public sealed class UserPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FinanceUserId { get; set; }
    public Guid FullWorthSpaceId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record PreferenceView(string Key, System.Text.Json.JsonElement Value, DateTimeOffset UpdatedAt);

public sealed class PreferenceStore(FullWorthDbContext db)
{
    // Keys are a small fixed allowlist so this cannot become an arbitrary per-user blob dump.
    public static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "dashboard.layout",
        "dashboard.layout.mobile",
        "navigation.mobile",
        "notifications.types",
        "analytics.savedAnalyses",
        "accounts.visuals",
        "account-groups.visuals",
        "transactions.seenAt",
    };
    public const int MaxValueBytes = 64 * 1024;

    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId, ct);

    public async Task<PreferenceView?> GetAsync(Guid userId, Guid fullWorthSpaceId, string key, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var row = await db.Set<UserPreference>().AsNoTracking()
            .SingleOrDefaultAsync(p => p.FinanceUserId == userId && p.FullWorthSpaceId == fullWorthSpaceId && p.Key == key, ct);
        // Nothing stored yet: return an empty OBJECT (not a default/Undefined JsonElement, which would
        // throw on serialization) so the client sees an empty layout rather than a 500.
        var json = row?.ValueJson ?? "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return new PreferenceView(key, doc.RootElement.Clone(), row?.UpdatedAt ?? DateTimeOffset.MinValue);
    }

    public async Task<bool> SetAsync(Guid userId, Guid fullWorthSpaceId, string key, string valueJson, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return false;
        var row = await db.Set<UserPreference>()
            .SingleOrDefaultAsync(p => p.FinanceUserId == userId && p.FullWorthSpaceId == fullWorthSpaceId && p.Key == key, ct);
        if (row is null)
        {
            row = new UserPreference { FinanceUserId = userId, FullWorthSpaceId = fullWorthSpaceId, Key = key };
            db.Add(row);
        }
        row.ValueJson = valueJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public static class PreferenceEndpoints
{
    public static IEndpointRouteBuilder MapPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences").WithTags("Preferences");

        group.MapGet("/{key}", async (string key, Guid fullWorthSpaceId, CurrentUserContext currentUser, PreferenceStore store, CancellationToken ct) =>
        {
            if (!PreferenceStore.AllowedKeys.Contains(key)) return Results.NotFound();
            var view = await store.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, key, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        });

        group.MapPut("/{key}", async (string key, Guid fullWorthSpaceId, System.Text.Json.JsonElement value, CurrentUserContext currentUser, PreferenceStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!PreferenceStore.AllowedKeys.Contains(key)) return Results.NotFound();
            var json = value.GetRawText();
            if (json.Length > PreferenceStore.MaxValueBytes) return Results.BadRequest(new { error = "Preference value too large." });
            return await store.SetAsync(currentUser.RequireUserId(), fullWorthSpaceId, key, json, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        return app;
    }
}
