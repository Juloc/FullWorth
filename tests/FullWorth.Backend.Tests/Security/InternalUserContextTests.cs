using System.Net;
using System.Reflection;
using System.Text.Json;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Security;

public sealed class InternalUserContextTests
{
    [Fact]
    public async Task ValidInternalKeyAndActiveFullWorthUserEstablishCurrentUserContext()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId);

        using var response = await client.SendAsync(CreateUserRequest(userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(userId, json.RootElement.GetProperty("userId").GetGuid());
        Assert.True(json.RootElement.GetProperty("isAuthenticated").GetBoolean());
    }

    [Fact]
    public async Task WrongInternalKeyIsDeniedBeforeUserContextIsTrusted()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId);

        using var request = CreateUserRequest(userId, "wrong-internal-key-that-is-long-enough-for-test");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingInternalKeyMissingUserAndMalformedUserAreDenied()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId);

        using (var missingKey = new HttpRequestMessage(HttpMethod.Get, TestContextPath))
        {
            missingKey.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
            using var response = await client.SendAsync(missingKey);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var missingUser = new HttpRequestMessage(HttpMethod.Get, TestContextPath))
        {
            missingUser.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
            using var response = await client.SendAsync(missingUser);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var malformedUser = new HttpRequestMessage(HttpMethod.Get, TestContextPath))
        {
            malformedUser.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
            malformedUser.Headers.Add("X-FullWorth-User-Id", "not-a-guid");
            using var response = await client.SendAsync(malformedUser);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task NonexistentAndInactiveFullWorthUsersAreDenied()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var inactiveUserId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(inactiveUserId, isActive: false);

        using (var nonexistent = await client.SendAsync(CreateUserRequest(Guid.NewGuid())))
            Assert.Equal(HttpStatusCode.Unauthorized, nonexistent.StatusCode);

        using (var inactive = await client.SendAsync(CreateUserRequest(inactiveUserId)))
            Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
    }

    [Fact]
    public async Task LegacyMasterAndReadKeysCannotAuthorizeApi()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using (var legacyMaster = new HttpRequestMessage(HttpMethod.Get, TestContextPath))
        {
            legacyMaster.Headers.Add("X-FullWorth-Key", BackendWebApplicationFactory.WriteKey);
            using var response = await client.SendAsync(legacyMaster);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var legacyRead = new HttpRequestMessage(HttpMethod.Get, TestContextPath))
        {
            legacyRead.Headers.Add("X-FullWorth-Read-Key", BackendWebApplicationFactory.ReadKey);
            using var response = await client.SendAsync(legacyRead);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task IngestAndNormalUserCredentialsRemainIsolated()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId);
        var internalPath = $"/internal/banking/connections/{Guid.NewGuid():D}/accounts/missing/sync-state";

        using (var ingest = new HttpRequestMessage(HttpMethod.Get, internalPath))
        {
            ingest.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
            using var response = await client.SendAsync(ingest);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var ingestOnApi = new HttpRequestMessage(HttpMethod.Get, TestContextPath))
        {
            ingestOnApi.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
            using var response = await client.SendAsync(ingestOnApi);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var userKeyOnIngest = new HttpRequestMessage(HttpMethod.Get, internalPath))
        {
            userKeyOnIngest.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
            userKeyOnIngest.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
            using var response = await client.SendAsync(userKeyOnIngest);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task ConcurrentRequestsKeepScopedCurrentUserContextsIsolated()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userA);
        await factory.SeedFullWorthUserAsync(userB);

        var results = await Task.WhenAll(ReadContextAsync(client, userA), ReadContextAsync(client, userB));
        Assert.Equal(userA, results[0]);
        Assert.Equal(userB, results[1]);
    }

    [Fact]
    public void CurrentUserContextIsScopedAndHasNoStaticMutableIdentity()
    {
        using var factory = new BackendWebApplicationFactory();
        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var first = scopeA.ServiceProvider.GetRequiredService<CurrentUserContext>();
        var sameScope = scopeA.ServiceProvider.GetRequiredService<CurrentUserContext>();
        var second = scopeB.ServiceProvider.GetRequiredService<CurrentUserContext>();

        Assert.Same(first, sameScope);
        Assert.NotSame(first, second);

        var staticMutableFields = typeof(CurrentUserContext)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly)
            .ToArray();
        Assert.Empty(staticMutableFields);
    }

    private const string TestContextPath = "/api/__test/current-user-context";

    private static HttpRequestMessage CreateUserRequest(Guid userId, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, TestContextPath);
        request.Headers.Add("X-FullWorth-Internal-Key", key ?? BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Guid> ReadContextAsync(HttpClient client, Guid userId)
    {
        using var request = CreateUserRequest(userId);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("userId").GetGuid();
    }
}
