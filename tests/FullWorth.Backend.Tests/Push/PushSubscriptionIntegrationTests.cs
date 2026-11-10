using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Push;

/// <summary>
/// Push subscriptions are per-user: each user manages only their own devices (Wave K2). Verifies
/// subscribe/list/revoke and that one user cannot see or revoke another user's device.
/// </summary>
public sealed class PushSubscriptionIntegrationTests
{
    [Fact]
    public async Task Subscriptions_AreScopedToTheOwningUser()
    {
        using var factory = new BackendWebApplicationFactory();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = userA, EmailNormalized = $"{userA:N}@EXAMPLE.COM", DisplayName = "Push A", IsActive = true },
                new FullWorthUser { Id = userB, EmailNormalized = $"{userB:N}@EXAMPLE.COM", DisplayName = "Push B", IsActive = true });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        // A subscribes a device.
        using var sub = await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", userA,
            new { endpoint = "https://push.example.com/a-1", p256dh = "p256dh-key", auth = "auth-key", deviceLabel = "A phone" }));
        Assert.Equal(HttpStatusCode.OK, sub.StatusCode);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // A sees it; B does not.
        Assert.Single(await ListAsync(client, userA));
        Assert.Empty(await ListAsync(client, userB));

        // B cannot revoke A's device.
        using var foreignDelete = await client.SendAsync(Request(HttpMethod.Delete, $"/api/push/subscriptions/{id}", userB));
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);

        // A revokes its own device.
        using var ownDelete = await client.SendAsync(Request(HttpMethod.Delete, $"/api/push/subscriptions/{id}", userA));
        Assert.Equal(HttpStatusCode.NoContent, ownDelete.StatusCode);
        Assert.Empty(await ListAsync(client, userA));
    }

    [Fact]
    public async Task The_test_message_reports_a_missing_device_and_a_failed_delivery_instead_of_sent()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = user, EmailNormalized = $"{user:N}@EXAMPLE.COM", DisplayName = "Push", IsActive = true });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var none = await client.SendAsync(Request(HttpMethod.Post, "/api/push/test", user));
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Contains("no_devices", await none.Content.ReadAsStringAsync());

        // A subscription the push service cannot deliver to (here: keys that are no keys) must not be reported as sent.
        using var sub = await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", user,
            new { endpoint = "https://127.0.0.1:1/push", p256dh = "not-a-key", auth = "not-an-auth" }));
        Assert.Equal(HttpStatusCode.OK, sub.StatusCode);

        using var failed = await client.SendAsync(Request(HttpMethod.Post, "/api/push/test", user));
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Contains("delivery_failed", await failed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Subscribe_RejectsIncompletePayload()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = user, EmailNormalized = $"{user:N}@EXAMPLE.COM", DisplayName = "Push", IsActive = true });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", user,
            new { endpoint = "https://push.example.com/x", p256dh = "", auth = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, Guid userId)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, "/api/push/subscriptions", userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
