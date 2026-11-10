using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Preferences;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// Tells the owners of an account that new bookings arrived - once per fetch and person, grouped when there are
/// several, because the bank is only asked every few hours and a fetch can bring a dozen payments at once.
///
/// What counts as news is decided by the caller (rows this fetch created, on an account that already had
/// history, whose pending entry was not announced before). What this adds is the rest of the filter: nothing
/// older than a week (a reconnection after a long gap is not a payment right now), nothing the user marked as
/// ignored, no move between the user's own accounts, and only to the people who own the account - a push must
/// not show a household member's payments to someone who cannot see that account.
///
/// The amount is shown only if the person allowed it (<c>notifications.display</c> → <c>showAmounts</c>): a
/// lock screen is read by whoever stands next to the phone.
/// </summary>
public sealed class NewBookingNotificationService(
    FullWorthDbContext db,
    PreferenceStore preferences,
    NotificationDispatcher dispatcher,
    ILogger<NewBookingNotificationService> logger)
{
    public const string DisplayPreferenceKey = "notifications.display";
    private const int RecentDays = 7;

    public async Task NotifyAsync(Guid fullWorthSpaceId, IReadOnlyCollection<Guid> transactionIds, CancellationToken ct)
    {
        if (transactionIds.Count == 0) return;
        try
        {
            await NotifyCoreAsync(fullWorthSpaceId, transactionIds, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "New booking notification failed for space {SpaceId}", fullWorthSpaceId);
        }
    }

    private async Task NotifyCoreAsync(Guid fullWorthSpaceId, IReadOnlyCollection<Guid> transactionIds, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var since = today.AddDays(-RecentDays);
        var rows = await db.Transactions.AsNoTracking()
            .Where(x => transactionIds.Contains(x.Id) && !x.IsIgnored && !x.IsTransfer && x.TransferGroupId == null && x.Amount != 0m
                        && (x.BookingDate ?? x.ValueDate ?? today) >= since)
            .Select(x => new
            {
                x.AccountId,
                Name = x.Counterparty ?? x.Description ?? string.Empty,
                x.Amount,
                x.Currency,
                Date = x.BookingDate ?? x.ValueDate
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        var accountIds = rows.Select(x => x.AccountId).Distinct().ToList();
        var owners = await db.AccountOwners.AsNoTracking()
            .Where(x => accountIds.Contains(x.AccountId))
            .Select(x => new { x.AccountId, x.UserId })
            .ToListAsync(ct);

        foreach (var userId in owners.Select(x => x.UserId).Distinct())
        {
            var ownAccounts = owners.Where(x => x.UserId == userId).Select(x => x.AccountId).ToHashSet();
            var lines = rows
                .Where(x => ownAccounts.Contains(x.AccountId))
                .OrderByDescending(x => x.Date)
                .Select(x => new NewBookingLine(x.Name, x.Amount, x.Currency))
                .ToList();
            if (lines.Count == 0) continue;

            var showAmounts = await ShowAmountsAsync(userId, fullWorthSpaceId, ct);
            await dispatcher.DispatchAsync(
                userId, fullWorthSpaceId, NotificationTypes.TransactionsNew,
                NotificationMessages.NewBookings(lines, showAmounts), dedupKey: null, ct);
        }
    }

    private async Task<bool> ShowAmountsAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var view = await preferences.GetAsync(userId, fullWorthSpaceId, DisplayPreferenceKey, ct);
        return view is { Value: { ValueKind: JsonValueKind.Object } value }
            && value.TryGetProperty("showAmounts", out var flag)
            && flag.ValueKind == JsonValueKind.True;
    }
}
