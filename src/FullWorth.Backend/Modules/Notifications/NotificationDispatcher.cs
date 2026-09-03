using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Push;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// Delivers a notification to one user: it honors the user's per-type toggle (notifications.types), dedups
/// once-only types, then hands off to the (VAPID-signed, no-op-until-configured) push sender. Best-effort
/// by contract — every failure is logged and swallowed so a notification can never break the sync/booking
/// that triggered it.
/// </summary>
public sealed class NotificationDispatcher(
    FullWorthDbContext db,
    PreferenceStore preferences,
    IPushSender push,
    ILogger<NotificationDispatcher> logger)
{
    private const string PreferenceKey = "notifications.types";

    /// <param name="dedupKey">Null for state-transition types (no dedup row). Non-null makes the send
    /// once-only per (user, type, key).</param>
    /// <returns>True if the type was ENABLED for the user (whether freshly sent or already deduped);
    /// false if suppressed by the user's toggle or the attempt errored. Callers deciding between sibling
    /// alerts (budget over vs near) use this so a disabled "over" doesn't also swallow "near".</returns>
    public async Task<bool> DispatchAsync(Guid userId, Guid fullWorthSpaceId, string type, PushMessage message, string? dedupKey, CancellationToken ct)
    {
        try
        {
            if (!await IsTypeEnabledAsync(userId, fullWorthSpaceId, type, ct)) return false;
            if (dedupKey is not null && await AlreadySentAsync(userId, type, dedupKey, ct)) return true;

            await push.SendToUserAsync(userId, message, ct);

            if (dedupKey is not null) await RecordSentAsync(userId, fullWorthSpaceId, type, dedupKey, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification dispatch failed for {Type} to {UserId}", type, userId);
            return false;
        }
    }

    /// <summary>Records a dedup marker WITHOUT sending — e.g. a "budget over" alert marks the sibling
    /// "budget near" occurrence as already handled so a later dip below the limit can't buzz "near".</summary>
    public async Task MarkDedupAsync(Guid userId, Guid fullWorthSpaceId, string type, string dedupKey, CancellationToken ct)
    {
        try
        {
            if (!await AlreadySentAsync(userId, type, dedupKey, ct))
                await RecordSentAsync(userId, fullWorthSpaceId, type, dedupKey, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification dedup mark failed for {Type} to {UserId}", type, userId);
        }
    }

    private async Task<bool> IsTypeEnabledAsync(Guid userId, Guid fullWorthSpaceId, string type, CancellationToken ct)
    {
        // GetAsync returns null for a non-member — such a user must never be notified. Otherwise the value
        // is a { types: { <type>: bool } } map where absent/true = send, false = suppress (mirrors the
        // frontend default `prefs[t] !== false`).
        var view = await preferences.GetAsync(userId, fullWorthSpaceId, PreferenceKey, ct);
        if (view is null) return false;
        if (view.Value.ValueKind == JsonValueKind.Object
            && view.Value.TryGetProperty("types", out var types)
            && types.ValueKind == JsonValueKind.Object
            && types.TryGetProperty(type, out var flag)
            && flag.ValueKind == JsonValueKind.False)
            return false;
        return true;
    }

    private Task<bool> AlreadySentAsync(Guid userId, string type, string dedupKey, CancellationToken ct) =>
        db.NotificationDedups.AsNoTracking()
            .AnyAsync(x => x.FinanceUserId == userId && x.Type == type && x.DedupKey == dedupKey, ct);

    private async Task RecordSentAsync(Guid userId, Guid fullWorthSpaceId, string type, string dedupKey, CancellationToken ct)
    {
        var row = new NotificationDedup
        {
            FinanceUserId = userId,
            FullWorthSpaceId = fullWorthSpaceId,
            Type = type,
            DedupKey = dedupKey
        };
        db.NotificationDedups.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent dispatch inserted the same (user, type, key) first — the unique index rejected
            // this one. The user was already notified; detach just this row so the context stays usable.
            db.Entry(row).State = EntityState.Detached;
        }
    }
}
