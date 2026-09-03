using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPush;

namespace FullWorth.Backend.Modules.Push;

/// <summary>A registered Web Push endpoint for one of a user's devices.</summary>
public sealed class PushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FinanceUserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public string? DeviceLabel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record PushDeviceView(Guid Id, string Endpoint, string? DeviceLabel, DateTimeOffset CreatedAt);
public sealed record PushSubscribeRequest(string Endpoint, string P256dh, string Auth, string? DeviceLabel);

/// <summary>A notification to deliver; channel-agnostic so email/other channels can reuse it later.</summary>
public sealed record PushMessage(string Title, string Body, string? Url = null);

public sealed class PushOptions
{
    public const string SectionName = "Push";
    public string VapidPublicKey { get; set; } = string.Empty;
    public string VapidPrivateKey { get; set; } = string.Empty;
    public string VapidSubject { get; set; } = "mailto:admin@localhost";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(VapidPublicKey) && !string.IsNullOrWhiteSpace(VapidPrivateKey);
}

public sealed class PushSubscriptionStore(FullWorthDbContext db)
{
    public async Task<PushDeviceView> SubscribeAsync(Guid userId, PushSubscribeRequest request, CancellationToken ct)
    {
        var existing = await db.PushDevices.SingleOrDefaultAsync(d => d.FinanceUserId == userId && d.Endpoint == request.Endpoint, ct);
        if (existing is null)
        {
            existing = new PushDevice { FinanceUserId = userId, Endpoint = request.Endpoint };
            db.PushDevices.Add(existing);
        }
        existing.P256dh = request.P256dh;
        existing.Auth = request.Auth;
        existing.DeviceLabel = string.IsNullOrWhiteSpace(request.DeviceLabel) ? null : request.DeviceLabel.Trim();
        await db.SaveChangesAsync(ct);
        return new PushDeviceView(existing.Id, existing.Endpoint, existing.DeviceLabel, existing.CreatedAt);
    }

    public Task<List<PushDeviceView>> ListAsync(Guid userId, CancellationToken ct) =>
        db.PushDevices.AsNoTracking().Where(d => d.FinanceUserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new PushDeviceView(d.Id, d.Endpoint, d.DeviceLabel, d.CreatedAt))
            .ToListAsync(ct);

    public async Task<bool> RevokeAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Id == id && d.FinanceUserId == userId, ct);
        if (device is null) return false;                 // unknown/foreign -> caller maps to 404
        db.PushDevices.Remove(device);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

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

public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/push").WithTags("Push");

        group.MapGet("/vapid-public-key", (IOptions<PushOptions> options) =>
            Results.Ok(new { publicKey = options.Value.IsConfigured ? options.Value.VapidPublicKey : null }));

        group.MapGet("/subscriptions", async (CurrentUserContext currentUser, PushSubscriptionStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(currentUser.RequireUserId(), ct)));

        group.MapPost("/subscriptions", async (PushSubscribeRequest request, CurrentUserContext currentUser, PushSubscriptionStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint) || string.IsNullOrWhiteSpace(request.P256dh) || string.IsNullOrWhiteSpace(request.Auth))
                return Results.Problem(detail: "endpoint, p256dh and auth are required.", statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok(await store.SubscribeAsync(currentUser.RequireUserId(), request, ct));
        });

        group.MapDelete("/subscriptions/{id:guid}", async (Guid id, CurrentUserContext currentUser, PushSubscriptionStore store, CancellationToken ct) =>
            await store.RevokeAsync(currentUser.RequireUserId(), id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
