using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.BankConnections;

public sealed class BankConnectionAuthorizationIntegrationTests
{
    [Fact]
    public async Task MemberCanReadSafeConnectionStatusWithoutProviderInternals()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/bank-connections/{scenario.ConnectionA}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.Member));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(scenario.ConnectionA, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("E9 Bank A", json.RootElement.GetProperty("institutionName").GetString());
        Assert.Equal("error", json.RootElement.GetProperty("healthStatus").GetString());
        Assert.Null(json.RootElement.GetProperty("daysUntilExpiry").GetString());
        Assert.DoesNotContain("authorizationState", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorizationId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerSessionId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastError", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-state-a", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-auth-a", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-session-a", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-provider-error-a", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListContainsOnlySelectedFullWorthSpaceForCurrentMember()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/bank-connections?fullWorthSpaceId={scenario.SpaceA}",
            scenario.Member));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(scenario.ConnectionA, item.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task CrossSpaceAndNonMemberConnectionAccessReturnsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var cross = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/bank-connections/{scenario.ConnectionB}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.Member));
        using var missing = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/bank-connections/{Guid.NewGuid():D}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.Member));
        using var outsider = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/bank-connections/{scenario.ConnectionA}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.Outside));

        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal(missing.StatusCode, cross.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    [Fact]
    public async Task NonMemberCannotReadKnownBankConnectionById()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/bank-connections/{scenario.ConnectionA}?fullWorthSpaceId={scenario.SpaceA}",
            scenario.Outside));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NonMemberCannotEnumerateKnownFullWorthSpaceConnections()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/bank-connections?fullWorthSpaceId={scenario.SpaceA}",
            scenario.Outside));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InternalBankingPathStillRequiresIngestKeyAndRetainsTechnicalFields()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var denied = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            "/internal/banking/connections",
            scenario.Member));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/banking/connections");
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        using var allowed = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var body = await allowed.Content.ReadAsStringAsync();
        Assert.Contains("secret-session-a", body, StringComparison.Ordinal);
        Assert.Contains("secret-state-a", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicBankConnectionCollectionHasNoMutationEndpoint()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/bank-connections?fullWorthSpaceId={scenario.SpaceA}",
            scenario.Owner));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"E9 {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = scenario.SpaceA, Name = "E9 Space A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.SpaceB, Name = "E9 Space B", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceB, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner });

            db.BankConnections.AddRange(
                Connection(scenario.ConnectionA, scenario.SpaceA, "E9 Bank A", "a"),
                Connection(scenario.ConnectionB, scenario.SpaceB, "E9 Bank B", "b"));
            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static BankConnection Connection(Guid id, Guid spaceId, string institution, string suffix) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Provider = "enable-banking",
        InstitutionName = institution,
        Country = "DE",
        AuthorizationState = $"secret-state-{suffix}",
        AuthorizationId = $"secret-auth-{suffix}",
        ProviderSessionId = $"secret-session-{suffix}",
        Status = "AUTHORIZED",
        LastError = $"private-provider-error-{suffix}",
        LastSyncedAt = DateTimeOffset.UtcNow.AddHours(-1),
        NextSyncAllowedAt = DateTimeOffset.UtcNow.AddHours(5)
    };

    private sealed record Scenario(
        Guid Owner,
        Guid Member,
        Guid Outside,
        Guid SpaceA,
        Guid SpaceB,
        Guid ConnectionA,
        Guid ConnectionB);
}
