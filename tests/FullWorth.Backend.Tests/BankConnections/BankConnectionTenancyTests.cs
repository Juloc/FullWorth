using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// P0.2 authority checks on the internal banking endpoints: owner-only authorization, mandatory
/// validated space on new connections (no LegacyId fallback), and one-time/expiring state consumption.
/// </summary>
public sealed class BankConnectionTenancyTests
{
    [Fact]
    public async Task Authorize_allowsOwner_forbidsMember_hidesForeignAndUnknown()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await Authorize(client, s.Owner, s.SpaceA, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorize(client, s.Member, s.SpaceA, null)).StatusCode);
        // Non-member of the space, and a completely unknown space, are both 404 (no existence oracle).
        Assert.Equal(HttpStatusCode.NotFound, (await Authorize(client, s.Outsider, s.SpaceA, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Authorize(client, s.Owner, Guid.NewGuid(), null)).StatusCode);
    }

    [Fact]
    public async Task Authorize_forConnection_requiresItBelongsToTheSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await Authorize(client, s.Owner, s.SpaceA, s.ConnectionA)).StatusCode);
        // Owner of A asking about A's connection while naming space B (which they also own) → 404.
        Assert.Equal(HttpStatusCode.NotFound, (await Authorize(client, s.Owner, s.SpaceB, s.ConnectionA)).StatusCode);
    }

    [Fact]
    public async Task Authorize_withoutUserHeader_is400()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/connections/authorize")
        {
            Content = JsonContent.Create(new { fullWorthSpaceId = s.SpaceA, connectionId = (Guid?)null })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NewConnection_withoutValidatedSpace_isRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await Upsert(client, new
        {
            id = (Guid?)null,
            provider = "enable-banking",
            institutionName = "X",
            country = "DE",
            status = "PENDING_AUTHORIZATION",
            consecutiveFailures = 0,
            fullWorthSpaceId = (Guid?)null // missing → must be rejected (no LegacyId fallback)
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NewConnection_withValidatedSpace_landsInThatSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await Upsert(client, new
        {
            id = (Guid?)null,
            provider = "enable-banking",
            institutionName = "Household Bank",
            country = "DE",
            authorizationState = "st-" + Guid.NewGuid().ToString("N"),
            status = "PENDING_AUTHORIZATION",
            consecutiveFailures = 0,
            fullWorthSpaceId = s.SpaceA,
            authorizationUserId = s.Owner,
            authorizationStateExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<BankConnection>();
        Assert.Equal(s.SpaceA, created!.FullWorthSpaceId);
        Assert.NotEqual(FullWorthSpaceDefaults.LegacyId, created.FullWorthSpaceId);
    }

    [Fact]
    public async Task ConsumeState_isOneTime_andRejectsExpired()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var liveState = "live-" + Guid.NewGuid().ToString("N");
        var expiredState = "exp-" + Guid.NewGuid().ToString("N");
        await factory.SeedAsync(async db =>
        {
            db.BankConnections.AddRange(
                Fresh(s.SpaceA, liveState, DateTimeOffset.UtcNow.AddMinutes(15)),
                Fresh(s.SpaceA, expiredState, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await db.SaveChangesAsync();
        });

        // Live state: first consume succeeds, second (replay) 404s.
        Assert.Equal(HttpStatusCode.OK, (await Consume(client, liveState)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Consume(client, liveState)).StatusCode);
        // Expired state: rejected.
        Assert.Equal(HttpStatusCode.NotFound, (await Consume(client, expiredState)).StatusCode);
    }

    [Fact]
    public async Task ConsumeState_clearsStateSoTheColumnCannotAccumulate()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var state = "c-" + Guid.NewGuid().ToString("N");
        await factory.SeedAsync(async db =>
        {
            db.BankConnections.Add(Fresh(s.SpaceA, state, DateTimeOffset.UtcNow.AddMinutes(15)));
            await db.SaveChangesAsync();
        });

        await Consume(client, state);

        await using var scope = factory.Services.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<FullWorth.Backend.Data.FullWorthDbContext>();
        Assert.False(await db2.BankConnections.AnyAsync(x => x.AuthorizationState == state));
    }

    private static Task<HttpResponseMessage> Authorize(HttpClient client, Guid userId, Guid spaceId, Guid? connectionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/connections/authorize")
        {
            Content = JsonContent.Create(new { fullWorthSpaceId = spaceId, connectionId })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Upsert(HttpClient client, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/connections/") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Consume(HttpClient client, string state)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/connections/consume-state") { Content = JsonContent.Create(new { state }) };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        return client.SendAsync(request);
    }

    private static BankConnection Fresh(Guid spaceId, string state, DateTimeOffset expires) => new()
    {
        FullWorthSpaceId = spaceId,
        Provider = "enable-banking",
        InstitutionName = "State Bank",
        Country = "DE",
        AuthorizationState = state,
        AuthorizationStateExpiresAt = expires,
        Status = "PENDING_AUTHORIZATION"
    };

    private sealed record Scenario(Guid Owner, Guid Member, Guid Outsider, Guid SpaceA, Guid SpaceB, Guid ConnectionA);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            foreach (var id in new[] { s.Owner, s.Member, s.Outsider })
                db.Users.Add(new FullWorthUser { Id = id, EmailNormalized = $"{id:N}@EX.COM".ToUpperInvariant(), DisplayName = $"U {id:N}", IsActive = true });
            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = s.SpaceA, Name = "Household", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = s.SpaceB, Name = "Other", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = s.SpaceA, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = s.SpaceA, UserId = s.Member, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = s.SpaceB, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner });
            db.BankConnections.Add(new BankConnection { Id = s.ConnectionA, FullWorthSpaceId = s.SpaceA, Provider = "enable-banking", InstitutionName = "A", Country = "DE", ProviderSessionId = "sess-a", Status = "AUTHORIZED" });
            await db.SaveChangesAsync();
        });
        return s;
    }
}
