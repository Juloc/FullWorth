using Microsoft.Extensions.Options;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Push;

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
