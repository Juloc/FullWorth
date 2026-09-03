using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FullWorth.Backend.Tests.Audit;

public sealed class AuditTests
{
    [Fact]
    public async Task SpaceCreationWritesAuditEvent()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(owner);

        Guid spaceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var spaces = scope.ServiceProvider.GetRequiredService<FullWorthSpaceService>();
            var space = await spaces.CreateAsync(owner, "Home", "EUR", CancellationToken.None);
            spaceId = space.Id;
        }

        await factory.SeedAsync(async db =>
        {
            var evt = await db.Set<AuditEvent>().AsNoTracking()
                .SingleAsync(x => x.FullWorthSpaceId == spaceId && x.Action == "space.created");
            Assert.Equal(owner, evt.ActorUserId);
            Assert.Equal("FullWorthSpace", evt.EntityType);
            Assert.Equal(spaceId, evt.EntityId);
        });
    }

    [Fact]
    public async Task AuditReadIsScopedToSpaceMembers()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(owner);
        await factory.SeedFullWorthUserAsync(member);
        await factory.SeedFullWorthUserAsync(outsider);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Set<FullWorthSpace>().Add(new FullWorthSpace { Id = spaceId, Name = "A", BaseCurrency = "EUR", CreatedAt = now, UpdatedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = owner, Role = FullWorthSpaceRoles.Owner, JoinedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = member, Role = FullWorthSpaceRoles.Member, JoinedAt = now });
            db.Set<AuditEvent>().Add(new AuditEvent { FullWorthSpaceId = spaceId, ActorUserId = owner, Action = "space.created", EntityType = "FullWorthSpace", EntityId = spaceId, OccurredAt = now });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();

        using var asOwner = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/audit?fullWorthSpaceId={spaceId}", owner));
        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
        var events = await asOwner.Content.ReadFromJsonAsync<List<AuditEventView>>();
        Assert.NotNull(events);
        Assert.Contains(events!, e => e.Action == "space.created" && e.ActorUserId == owner);

        using var asMember = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/audit?fullWorthSpaceId={spaceId}", member));
        Assert.Equal(HttpStatusCode.NotFound, asMember.StatusCode);

        // A valid, active user who is not a member of the space gets 404 (indistinguishable from
        // a nonexistent space) — no cross-space leakage.
        using var asOutsider = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/audit?fullWorthSpaceId={spaceId}", outsider));
        Assert.Equal(HttpStatusCode.NotFound, asOutsider.StatusCode);
    }

    [Fact]
    public async Task BankConnectionAuditEventNeverContainsProviderSecrets()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const string authorizationSecret = "top-secret-authorization";
        const string providerSessionSecret = "session-cookie-secret";
        const string errorSecret = "password=not-for-audit";
        await factory.SeedFullWorthUserAsync(owner);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Private", BaseCurrency = "EUR", CreatedAt = now, UpdatedAt = now });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = owner, Role = FullWorthSpaceRoles.Owner, JoinedAt = now });
            db.BankConnections.Add(new FullWorth.Backend.Modules.BankConnections.BankConnection { Id = connectionId, FullWorthSpaceId = spaceId, InstitutionName = "Bank" });
            await db.SaveChangesAsync();
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<FullWorth.Backend.Modules.BankConnections.BankConnectionStore>();
            await store.UpsertAsync(new(connectionId, "provider", "Bank", "DE", "state", authorizationSecret, providerSessionSecret, "ERROR", null, null, null, null, 1, errorSecret), CancellationToken.None);
        }

        await factory.SeedAsync(async db =>
        {
            var audit = await db.AuditEvents.SingleAsync(x => x.EntityId == connectionId && x.Action == "bank_connection.error");
            Assert.Null(audit.MetadataJson);
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/audit?fullWorthSpaceId={spaceId}", owner));
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(authorizationSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(providerSessionSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(errorSecret, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilterByActionReturnsOnlyMatchingAction()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await SeedSpaceWithEventsAsync(factory, owner, null, spaceId,
            Evt("budget.created", "Budget", now),
            Evt("category.created", "FinanceCategory", now.AddSeconds(-1)),
            Evt("budget.updated", "Budget", now.AddSeconds(-2)));

        using var client = factory.CreateClient();
        using var r = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/audit?fullWorthSpaceId={spaceId}&action=budget.created", owner));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var events = await r.Content.ReadFromJsonAsync<List<AuditEventView>>();
        Assert.Single(events!);
        Assert.Equal("budget.created", events![0].Action);
    }

    [Fact]
    public async Task FilterByEntityTypeReturnsOnlyMatchingEntityType()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await SeedSpaceWithEventsAsync(factory, owner, null, spaceId,
            Evt("budget.created", "Budget", now),
            Evt("category.created", "FinanceCategory", now.AddSeconds(-1)),
            Evt("budget.updated", "Budget", now.AddSeconds(-2)));

        using var client = factory.CreateClient();
        using var r = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/audit?fullWorthSpaceId={spaceId}&entityType=Budget", owner));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var events = await r.Content.ReadFromJsonAsync<List<AuditEventView>>();
        Assert.Equal(2, events!.Count);
        Assert.All(events, e => Assert.Equal("Budget", e.EntityType));
    }

    [Fact]
    public async Task FilterByActionAndEntityTypeCombine()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await SeedSpaceWithEventsAsync(factory, owner, null, spaceId,
            Evt("budget.created", "Budget", now),
            Evt("budget.updated", "Budget", now.AddSeconds(-1)),
            Evt("category.created", "FinanceCategory", now.AddSeconds(-2)));

        using var client = factory.CreateClient();
        using var r = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/audit?fullWorthSpaceId={spaceId}&action=budget.updated&entityType=Budget", owner));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var events = await r.Content.ReadFromJsonAsync<List<AuditEventView>>();
        Assert.Single(events!);
        Assert.Equal("budget.updated", events![0].Action);
    }

    [Fact]
    public async Task PagingBeforeCursorReturnsOlderPage()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await SeedSpaceWithEventsAsync(factory, owner, null, spaceId,
            Evt("budget.created", "Budget", now),
            Evt("budget.updated", "Budget", now.AddSeconds(-1)),
            Evt("budget.archived", "Budget", now.AddSeconds(-2)));

        using var client = factory.CreateClient();
        using var first = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/audit?fullWorthSpaceId={spaceId}&limit=2", owner));
        var firstPage = await first.Content.ReadFromJsonAsync<List<AuditEventView>>();
        Assert.Equal(2, firstPage!.Count);
        var cursor = firstPage[1];

        using var second = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/audit?fullWorthSpaceId={spaceId}&limit=2&before={Uri.EscapeDataString(cursor.OccurredAt.ToString("O"))}&beforeId={cursor.Id}", owner));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var olderPage = await second.Content.ReadFromJsonAsync<List<AuditEventView>>();
        Assert.Single(olderPage!);
        Assert.DoesNotContain(olderPage!, e => e.Id == firstPage[0].Id || e.Id == firstPage[1].Id);
    }

    [Fact]
    public async Task PagingTieOnOccurredAtIsStable()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var tie = DateTimeOffset.UtcNow;
        // Two events share OccurredAt; a third is older. Keyset paging must not skip or duplicate across the tie.
        await SeedSpaceWithEventsAsync(factory, owner, null, spaceId,
            Evt("budget.created", "Budget", tie),
            Evt("budget.updated", "Budget", tie),
            Evt("budget.archived", "Budget", tie.AddSeconds(-1)));

        using var client = factory.CreateClient();
        var seen = new List<Guid>();
        DateTimeOffset? before = null; Guid? beforeId = null;
        for (var i = 0; i < 3; i++)
        {
            var url = $"/api/audit?fullWorthSpaceId={spaceId}&limit=1";
            if (before is { } b) url += $"&before={Uri.EscapeDataString(b.ToString("O"))}&beforeId={beforeId}";
            using var r = await client.SendAsync(UserRequest(HttpMethod.Get, url, owner));
            r.EnsureSuccessStatusCode();
            var page = await r.Content.ReadFromJsonAsync<List<AuditEventView>>();
            Assert.Single(page!);
            var last = page![0];
            seen.Add(last.Id);
            before = last.OccurredAt; beforeId = last.Id;
        }
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task FilteringAndPagingStillOwnerOnly()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await factory.SeedFullWorthUserAsync(outsider);
        await SeedSpaceWithEventsAsync(factory, owner, member, spaceId, Evt("space.created", "FullWorthSpace", now));

        using var client = factory.CreateClient();
        var query = $"/api/audit?fullWorthSpaceId={spaceId}&action=space.created&limit=1&before={Uri.EscapeDataString(now.ToString("O"))}&beforeId={Guid.NewGuid()}";

        using var asMember = await client.SendAsync(UserRequest(HttpMethod.Get, query, member));
        Assert.Equal(HttpStatusCode.NotFound, asMember.StatusCode);
        using var asOutsider = await client.SendAsync(UserRequest(HttpMethod.Get, query, outsider));
        Assert.Equal(HttpStatusCode.NotFound, asOutsider.StatusCode);
    }

    private static AuditEvent Evt(string action, string entityType, DateTimeOffset at) =>
        new() { Id = Guid.NewGuid(), ActorUserId = null, Action = action, EntityType = entityType, EntityId = Guid.NewGuid(), OccurredAt = at };

    private static async Task SeedSpaceWithEventsAsync(BackendWebApplicationFactory factory, Guid owner, Guid? member, Guid spaceId, params AuditEvent[] events)
    {
        await factory.SeedFullWorthUserAsync(owner);
        if (member is { } m) await factory.SeedFullWorthUserAsync(m);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Set<FullWorthSpace>().Add(new FullWorthSpace { Id = spaceId, Name = "A", BaseCurrency = "EUR", CreatedAt = now, UpdatedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = owner, Role = FullWorthSpaceRoles.Owner, JoinedAt = now });
            if (member is { } m2) db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = m2, Role = FullWorthSpaceRoles.Member, JoinedAt = now });
            foreach (var e in events) { e.FullWorthSpaceId = spaceId; db.Set<AuditEvent>().Add(e); }
            await db.SaveChangesAsync();
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record AuditEventView(Guid Id, Guid? ActorUserId, string Action, string EntityType, Guid? EntityId, DateTimeOffset OccurredAt);
}
