using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPush;

namespace FullWorth.Backend.Modules.Push;

/// <summary>A registered Web Push endpoint for one of a user's devices.</summary>

/// <summary>Sends notifications to a user's registered devices. No-op (logged) until VAPID is configured.</summary>
public interface IPushSender
{
    Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct);
}

public sealed class VapidPushSender(FullWorthDbContext db, IOptions<PushOptions> options, ILogger<VapidPushSender> logger) : IPushSender
{
    public async Task SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.IsConfigured)
        {
            logger.LogDebug("Web Push not configured (no VAPID keys); skipping notification to {UserId}", userId);
            return;
        }

        var devices = await db.PushDevices.AsNoTracking().Where(d => d.FinanceUserId == userId).ToListAsync(ct);
        if (devices.Count == 0) return;

        var payload = JsonSerializer.Serialize(new { title = message.Title, body = message.Body, url = message.Url });
        var vapid = new VapidDetails(opts.VapidSubject, opts.VapidPublicKey, opts.VapidPrivateKey);
        var client = new WebPushClient();

        foreach (var device in devices)
        {
            try
            {
                await client.SendNotificationAsync(new WebPush.PushSubscription(device.Endpoint, device.P256dh, device.Auth), payload, vapid);
            }
            catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
            {
                // The browser dropped this subscription — remove it so we stop trying.
                await db.PushDevices.Where(d => d.Id == device.Id).ExecuteDeleteAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Web Push delivery failed for device {DeviceId}", device.Id);
            }
        }
    }
}
