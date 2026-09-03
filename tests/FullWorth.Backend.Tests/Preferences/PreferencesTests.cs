using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Preferences;

/// <summary>
/// Per-user, per-space UI preferences (UI_UX_SPEC §22): round-trips a dashboard layout, stays scoped
/// to the owning user + space, rejects unknown keys, and never leaks another user's layout.
/// </summary>
public sealed class PreferencesTests
{
    [Fact]
    public async Task DashboardLayoutRoundTripsPerUserAndSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var layout = JsonSerializer.SerializeToElement(new { widgets = new[] { new { type = "net-worth", w = 8 } }, mode = "shared" });
        using var put = await client.SendAsync(Req(HttpMethod.Put, $"/api/preferences/dashboard.layout?fullWorthSpaceId={s.Space}", s.User, layout));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        using var get = await client.SendAsync(Req(HttpMethod.Get, $"/api/preferences/dashboard.layout?fullWorthSpaceId={s.Space}", s.User));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("shared", body.GetProperty("value").GetProperty("mode").GetString());
        Assert.Equal("net-worth", body.GetProperty("value").GetProperty("widgets")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task NotificationTypePreferencesRoundTrip()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var value = JsonSerializer.SerializeToElement(new { types = new { bank_reauth = true, budget_over = false } });
        using var put = await client.SendAsync(Req(HttpMethod.Put, $"/api/preferences/notifications.types?fullWorthSpaceId={s.Space}", s.User, value));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        using var get = await client.SendAsync(Req(HttpMethod.Get, $"/api/preferences/notifications.types?fullWorthSpaceId={s.Space}", s.User));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        var types = body.GetProperty("value").GetProperty("types");
        Assert.True(types.GetProperty("bank_reauth").GetBoolean());
        Assert.False(types.GetProperty("budget_over").GetBoolean());
    }

    [Fact]
    public async Task AnotherUserDoesNotSeeTheLayoutAndCannotWriteToAForeignSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var layout = JsonSerializer.SerializeToElement(new { widgets = Array.Empty<object>(), marker = "user-a" });
        await client.SendAsync(Req(HttpMethod.Put, $"/api/preferences/dashboard.layout?fullWorthSpaceId={s.Space}", s.User, layout));

        // The other user is a member of a DIFFERENT space and must not read A's space layout.
        using var foreignRead = await client.SendAsync(Req(HttpMethod.Get, $"/api/preferences/dashboard.layout?fullWorthSpaceId={s.Space}", s.Other));
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);

        // ...and cannot write into A's space either.
        using var foreignWrite = await client.SendAsync(Req(HttpMethod.Put, $"/api/preferences/dashboard.layout?fullWorthSpaceId={s.Space}", s.Other, layout));
        Assert.Equal(HttpStatusCode.NotFound, foreignWrite.StatusCode);
    }

    [Fact]
    public async Task UnknownPreferenceKeyIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var value = JsonSerializer.SerializeToElement(new { x = 1 });
        using var put = await client.SendAsync(Req(HttpMethod.Put, $"/api/preferences/evil.blob?fullWorthSpaceId={s.Space}", s.User, value));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task MissingLayoutReturnsAnEmptyPlaceholderNotAnError()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var get = await client.SendAsync(Req(HttpMethod.Get, $"/api/preferences/dashboard.layout?fullWorthSpaceId={s.Space}", s.User));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode); // member, but nothing stored yet
    }

    private sealed record Scenario(Guid User, Guid Other, Guid Space, Guid OtherSpace);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = s.User, EmailNormalized = $"{s.User:N}@EX.COM".ToUpperInvariant(), DisplayName = "A", IsActive = true },
                new FullWorthUser { Id = s.Other, EmailNormalized = $"{s.Other:N}@EX.COM".ToUpperInvariant(), DisplayName = "B", IsActive = true });
            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = s.Space, Name = "A space", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = s.OtherSpace, Name = "B space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.User, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = s.OtherSpace, UserId = s.Other, Role = FullWorthSpaceRoles.Owner });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, Guid userId, JsonElement? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body.HasValue) request.Content = JsonContent.Create(body.Value);
        return request;
    }
}
