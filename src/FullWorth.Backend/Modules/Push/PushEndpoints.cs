using Microsoft.Extensions.Options;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Push;

public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/push").WithTags("Push");

        group.MapGet("/vapid-public-key", async (PushKeyStore keys, CancellationToken ct) =>
            Results.Ok(new { publicKey = (await keys.GetAsync(ct)).PublicKey }));

        group.MapGet("/subscriptions", async (CurrentUserContext currentUser, PushSubscriptionStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(currentUser.RequireUserId(), ct)));

        group.MapPost("/subscriptions", async (PushSubscribeRequest request, CurrentUserContext currentUser, PushSubscriptionStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint) || string.IsNullOrWhiteSpace(request.P256dh) || string.IsNullOrWhiteSpace(request.Auth))
                return Results.Problem(detail: "endpoint, p256dh and auth are required.", statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok(await store.SubscribeAsync(currentUser.RequireUserId(), request, ct));
        });

        // A message to the caller's own devices, so the notification page can show that the whole chain works.
        // Not an alert type: it ignores the per-type toggles and goes only to the person who asked.
        group.MapPost("/test", async (CurrentUserContext currentUser, PushSubscriptionStore store, IPushSender sender, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var devices = await store.ListAsync(userId, ct);
            if (devices.Count == 0) return Results.Conflict(new { error = "no_devices" });
            await sender.SendToUserAsync(userId, new PushMessage("FullWorth", "Benachrichtigungen funktionieren auf diesem Gerät.", "/notifications"), ct);
            return Results.Ok(new { devices = devices.Count });
        });

        group.MapDelete("/subscriptions/{id:guid}", async (Guid id, CurrentUserContext currentUser, PushSubscriptionStore store, CancellationToken ct) =>
            await store.RevokeAsync(currentUser.RequireUserId(), id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
