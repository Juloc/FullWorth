using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Tests.Infrastructure;
using Xunit;

namespace FullWorth.Backend.Tests.Merchants;

public sealed class MerchantTests
{
    [Fact]
    public async Task CreateMerchantAddAliasAndResolve()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants?fullWorthSpaceId={s.Space}", s.Owner,
            new { name = "Acme Corp" }));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var merchant = await create.Content.ReadFromJsonAsync<MerchantView>();
        Assert.NotNull(merchant);

        using var alias = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants/{merchant!.Id}/aliases?fullWorthSpaceId={s.Space}", s.Owner,
            new { alias = "Acme Market" }));
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);

        using var resolve = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/merchants/resolve?fullWorthSpaceId={s.Space}&counterparty={Uri.EscapeDataString("Acme Market Berlin 1234")}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        var view = await resolve.Content.ReadFromJsonAsync<ResolveView>();
        Assert.Equal("ACME MARKET BERLIN 1234", view!.Normalized);
        Assert.Equal(merchant.Id, view.MerchantId);
        Assert.Equal("Acme Corp", view.MerchantName);
    }

    [Fact]
    public async Task ResolveWithNoMatchReturnsNormalizedOnly()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var resolve = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/merchants/resolve?fullWorthSpaceId={s.Space}&counterparty={Uri.EscapeDataString("Unknown Shop")}", s.Member));
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        var view = await resolve.Content.ReadFromJsonAsync<ResolveView>();
        Assert.Equal("UNKNOWN SHOP", view!.Normalized);
        Assert.Null(view.MerchantId);
    }

    [Fact]
    public async Task NonOwnerCannotManageAndOutsiderCannotRead()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var memberCreate = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants?fullWorthSpaceId={s.Space}", s.Member,
            new { name = "Nope" }));
        Assert.Equal(HttpStatusCode.Forbidden, memberCreate.StatusCode);

        // A member may still read/resolve.
        using var memberList = await client.SendAsync(Request(HttpMethod.Get, $"/api/merchants?fullWorthSpaceId={s.Space}", s.Member));
        Assert.Equal(HttpStatusCode.OK, memberList.StatusCode);

        // A valid user who is not a member of the space sees nothing (404).
        using var outsiderList = await client.SendAsync(Request(HttpMethod.Get, $"/api/merchants?fullWorthSpaceId={s.Space}", s.Outsider));
        Assert.Equal(HttpStatusCode.NotFound, outsiderList.StatusCode);
    }

    [Fact]
    public async Task RenameUpdatesNameAndNormalization()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var id = await CreateMerchantAsync(client, s, "Acme Corp");

        using var rename = await client.SendAsync(Request(HttpMethod.Put, $"/api/merchants/{id}?fullWorthSpaceId={s.Space}", s.Owner, new { name = "Acme Inc" }));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var view = await rename.Content.ReadFromJsonAsync<MerchantView>();
        Assert.Equal("Acme Inc", view!.Name);
        Assert.Equal("ACME INC", view.NormalizedName);
    }

    [Fact]
    public async Task RenameToSameMerchantCasingSucceeds()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var id = await CreateMerchantAsync(client, s, "Acme Corp");

        // Same normalized value, different display casing — allowed because the dup check excludes self.
        using var rename = await client.SendAsync(Request(HttpMethod.Put, $"/api/merchants/{id}?fullWorthSpaceId={s.Space}", s.Owner, new { name = "ACME corp" }));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var view = await rename.Content.ReadFromJsonAsync<MerchantView>();
        Assert.Equal("ACME corp", view!.Name);
    }

    [Fact]
    public async Task RenameToDuplicateNameIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await CreateMerchantAsync(client, s, "Acme");
        var beta = await CreateMerchantAsync(client, s, "Beta");

        using var rename = await client.SendAsync(Request(HttpMethod.Put, $"/api/merchants/{beta}?fullWorthSpaceId={s.Space}", s.Owner, new { name = "acme" }));
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
    }

    [Fact]
    public async Task RenameEmptyNameIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var id = await CreateMerchantAsync(client, s, "Acme");

        using var rename = await client.SendAsync(Request(HttpMethod.Put, $"/api/merchants/{id}?fullWorthSpaceId={s.Space}", s.Owner, new { name = "   " }));
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
    }

    [Fact]
    public async Task RenameMissingMerchantIsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var rename = await client.SendAsync(Request(HttpMethod.Put, $"/api/merchants/{Guid.NewGuid()}?fullWorthSpaceId={s.Space}", s.Owner, new { name = "Ghost" }));
        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
    }

    [Fact]
    public async Task RenameByNonOwnerIsForbidden()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var id = await CreateMerchantAsync(client, s, "Acme");

        using var rename = await client.SendAsync(Request(HttpMethod.Put, $"/api/merchants/{id}?fullWorthSpaceId={s.Space}", s.Member, new { name = "Nope" }));
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
    }

    [Fact]
    public async Task MergeMovesAliasesAndDeletesSource()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var target = await CreateMerchantAsync(client, s, "Acme");
        await AddAliasAsync(client, s, target, "Acme Market");
        var source = await CreateMerchantAsync(client, s, "Acme Old");
        await AddAliasAsync(client, s, source, "Acme Shop");

        using var merge = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants/{target}/merge?fullWorthSpaceId={s.Space}", s.Owner, new { sourceMerchantId = source }));
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
        var view = await merge.Content.ReadFromJsonAsync<MerchantView>();
        Assert.Equal(target, view!.Id);

        // Source is gone: exactly one merchant remains.
        using var list = await client.SendAsync(Request(HttpMethod.Get, $"/api/merchants?fullWorthSpaceId={s.Space}", s.Owner));
        var all = await list.Content.ReadFromJsonAsync<List<MerchantView>>();
        Assert.Single(all!);

        // The source's alias now resolves to the target, as does the source's own name, and the target's original alias still works.
        Assert.Equal(target, await ResolveMerchantAsync(client, s, "Acme Shop 12"));
        Assert.Equal(target, await ResolveMerchantAsync(client, s, "Acme Old"));
        Assert.Equal(target, await ResolveMerchantAsync(client, s, "Acme Market Berlin"));
    }

    [Fact]
    public async Task MergeIntoSelfIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var id = await CreateMerchantAsync(client, s, "Acme");

        using var merge = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants/{id}/merge?fullWorthSpaceId={s.Space}", s.Owner, new { sourceMerchantId = id }));
        Assert.Equal(HttpStatusCode.BadRequest, merge.StatusCode);
    }

    [Fact]
    public async Task MergeMissingSourceIsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var target = await CreateMerchantAsync(client, s, "Acme");

        using var merge = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants/{target}/merge?fullWorthSpaceId={s.Space}", s.Owner, new { sourceMerchantId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.NotFound, merge.StatusCode);
    }

    [Fact]
    public async Task MergeMissingTargetIsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var source = await CreateMerchantAsync(client, s, "Acme");

        using var merge = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants/{Guid.NewGuid()}/merge?fullWorthSpaceId={s.Space}", s.Owner, new { sourceMerchantId = source }));
        Assert.Equal(HttpStatusCode.NotFound, merge.StatusCode);
    }

    [Fact]
    public async Task MergeByNonOwnerIsForbidden()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var target = await CreateMerchantAsync(client, s, "Acme");
        var source = await CreateMerchantAsync(client, s, "Beta");

        using var merge = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants/{target}/merge?fullWorthSpaceId={s.Space}", s.Member, new { sourceMerchantId = source }));
        Assert.Equal(HttpStatusCode.Forbidden, merge.StatusCode);
    }

    [Fact]
    public async Task MergeWhenSourceNameAlreadyAliasedSucceedsWithoutDuplicate()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var target = await CreateMerchantAsync(client, s, "Acme");
        await AddAliasAsync(client, s, target, "Acme Old");        // "ACME OLD" already an alias in the space
        var source = await CreateMerchantAsync(client, s, "Acme Old"); // source's normalized name collides with it

        // The guard must SKIP adding the source name as an alias (it already exists), so no duplicate-alias
        // insert violates the unique (space, normalizedAlias) index — the merge still succeeds.
        using var merge = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants/{target}/merge?fullWorthSpaceId={s.Space}", s.Owner, new { sourceMerchantId = source }));
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);

        using var list = await client.SendAsync(Request(HttpMethod.Get, $"/api/merchants?fullWorthSpaceId={s.Space}", s.Owner));
        var all = await list.Content.ReadFromJsonAsync<List<MerchantView>>();
        Assert.Single(all!); // source deleted, no 500
        Assert.Equal(target, await ResolveMerchantAsync(client, s, "Acme Old Berlin"));
    }

    private static async Task<Guid> CreateMerchantAsync(HttpClient client, Scenario s, string name)
    {
        using var r = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants?fullWorthSpaceId={s.Space}", s.Owner, new { name }));
        r.EnsureSuccessStatusCode();
        var m = await r.Content.ReadFromJsonAsync<MerchantView>();
        return m!.Id;
    }

    private static async Task AddAliasAsync(HttpClient client, Scenario s, Guid merchantId, string alias)
    {
        using var r = await client.SendAsync(Request(HttpMethod.Post, $"/api/merchants/{merchantId}/aliases?fullWorthSpaceId={s.Space}", s.Owner, new { alias }));
        r.EnsureSuccessStatusCode();
    }

    private static async Task<Guid?> ResolveMerchantAsync(HttpClient client, Scenario s, string counterparty)
    {
        using var r = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/merchants/resolve?fullWorthSpaceId={s.Space}&counterparty={Uri.EscapeDataString(counterparty)}", s.Owner));
        r.EnsureSuccessStatusCode();
        var view = await r.Content.ReadFromJsonAsync<ResolveView>();
        return view!.MerchantId;
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedFullWorthUserAsync(s.Owner);
        await factory.SeedFullWorthUserAsync(s.Member);
        await factory.SeedFullWorthUserAsync(s.Outsider);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Set<FullWorthSpace>().Add(new FullWorthSpace { Id = s.Space, Name = "Space", BaseCurrency = "EUR", CreatedAt = now, UpdatedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner, JoinedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Member, Role = FullWorthSpaceRoles.Member, JoinedAt = now });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scenario(Guid Space, Guid Owner, Guid Member, Guid Outsider);
    private sealed record MerchantView(Guid Id, string Name, string NormalizedName);
    private sealed record ResolveView(string? Normalized, Guid? MerchantId, string? MerchantName);
}
