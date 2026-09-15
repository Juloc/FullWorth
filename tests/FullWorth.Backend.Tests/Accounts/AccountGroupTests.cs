using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Xunit;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// Account groups (§8.1): members create/rename/delete named groups in their space; account owners
/// assign an account to a group. Group CRUD is space-member gated; assignment is account-owner gated.
/// Seit #125 hat jeder Space eine Standardgruppe: sie entsteht beim ersten Lesen der Liste, ein Konto
/// ohne eigene Gruppe landet in ihr, und sie laesst sich nicht loeschen. Eine andere Gruppe zu loeschen
/// schiebt deren Konten dorthin, statt sie gruppenlos zu machen - gruppenlos gibt es nicht mehr.
/// </summary>
public sealed class AccountGroupTests
{
    [Fact]
    public async Task MemberCreatesGroupAndItAppearsInList()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(Req(HttpMethod.Post, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner, new { name = "Savings", sortOrder = 1 }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var list = await client.SendAsync(Req(HttpMethod.Get, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner));
        var groups = await list.Content.ReadFromJsonAsync<List<AccountGroupDto>>();
        // Die eigene Gruppe und die Standardgruppe - und die eigene ist nicht die Standardgruppe.
        Assert.Equal(2, groups!.Count);
        var own = Assert.Single(groups.Where(group => group.Name == "Savings"));
        Assert.False(own.IsDefault);
        Assert.Single(groups.Where(group => group.IsDefault));
    }

    [Fact]
    public async Task EverySpaceHasExactlyOneDefaultGroupAndItSurvivesRepeatedReads()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Req(HttpMethod.Get, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner));
        var initial = await first.Content.ReadFromJsonAsync<List<AccountGroupDto>>();
        var standard = Assert.Single(initial!);
        Assert.True(standard.IsDefault);

        // Jeder weitere Aufruf legt NICHT noch eine an.
        using var second = await client.SendAsync(Req(HttpMethod.Get, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner));
        var again = await second.Content.ReadFromJsonAsync<List<AccountGroupDto>>();
        Assert.Equal(standard.Id, Assert.Single(again!).Id);

        // Und das vorhandene Konto steht darin, statt gruppenlos zu sein.
        var account = await GetAccountAsync(client, s, s.Owner);
        Assert.Equal(standard.Id, account.GroupId);
    }

    [Fact]
    public async Task DefaultGroupCannotBeDeleted()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var list = await client.SendAsync(Req(HttpMethod.Get, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner));
        var standard = Assert.Single((await list.Content.ReadFromJsonAsync<List<AccountGroupDto>>())!);

        using var del = await client.SendAsync(Req(HttpMethod.Delete, $"/api/account-groups/{standard.Id}?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);

        using var after = await client.SendAsync(Req(HttpMethod.Get, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(standard.Id, Assert.Single((await after.Content.ReadFromJsonAsync<List<AccountGroupDto>>())!).Id);
    }

    [Fact]
    public async Task NonMemberCannotCreateGroupInForeignSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(Req(HttpMethod.Post, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Outsider, new { name = "Nope" }));
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }

    [Fact]
    public async Task EmptyGroupNameIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(Req(HttpMethod.Post, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner, new { name = "   " }));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task OwnerAssignsAccountToGroupAndListReflectsIt()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var group = await CreateGroupAsync(client, s, "Daily");

        using var assign = await client.SendAsync(Req(HttpMethod.Put, $"/api/accounts/{s.Account}/group?fullWorthSpaceId={s.Space}", s.Owner, new { groupId = group }));
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

        var account = await GetAccountAsync(client, s, s.Owner);
        Assert.Equal(group, account.GroupId);
        Assert.Equal("Daily", account.GroupName);
    }

    [Fact]
    public async Task AssignNullGroupMovesAccountIntoTheDefaultGroup()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var group = await CreateGroupAsync(client, s, "Daily");
        await AssignAsync(client, s, s.Owner, group);

        using var clear = await client.SendAsync(Req(HttpMethod.Put, $"/api/accounts/{s.Account}/group?fullWorthSpaceId={s.Space}", s.Owner, new { groupId = (Guid?)null }));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        var account = await GetAccountAsync(client, s, s.Owner);
        Assert.Equal(await DefaultGroupIdAsync(client, s), account.GroupId);
    }

    [Fact]
    public async Task ViewerCannotAssignAndOutsiderGetsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var group = await CreateGroupAsync(client, s, "Daily");

        using var asViewer = await client.SendAsync(Req(HttpMethod.Put, $"/api/accounts/{s.Account}/group?fullWorthSpaceId={s.Space}", s.Viewer, new { groupId = group }));
        Assert.Equal(HttpStatusCode.Forbidden, asViewer.StatusCode);
        using var asOutsider = await client.SendAsync(Req(HttpMethod.Put, $"/api/accounts/{s.Account}/group?fullWorthSpaceId={s.Space}", s.Outsider, new { groupId = group }));
        Assert.Equal(HttpStatusCode.NotFound, asOutsider.StatusCode);
    }

    [Fact]
    public async Task AssigningGroupFromAnotherSpaceIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var assign = await client.SendAsync(Req(HttpMethod.Put, $"/api/accounts/{s.Account}/group?fullWorthSpaceId={s.Space}", s.Owner, new { groupId = s.OtherSpaceGroup }));
        Assert.Equal(HttpStatusCode.NotFound, assign.StatusCode);
    }

    [Fact]
    public async Task DeletingGroupMovesItsAccountsIntoTheDefaultGroup()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var group = await CreateGroupAsync(client, s, "Daily");
        await AssignAsync(client, s, s.Owner, group);

        using var del = await client.SendAsync(Req(HttpMethod.Delete, $"/api/account-groups/{group}?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var account = await GetAccountAsync(client, s, s.Owner); // weiterhin gelistet, nur woanders
        Assert.Equal(await DefaultGroupIdAsync(client, s), account.GroupId);
    }

    [Fact]
    public async Task RenameGroupUpdatesName()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var group = await CreateGroupAsync(client, s, "Old");

        using var rename = await client.SendAsync(Req(HttpMethod.Put, $"/api/account-groups/{group}?fullWorthSpaceId={s.Space}", s.Owner, new { name = "New", sortOrder = 3 }));
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);

        using var list = await client.SendAsync(Req(HttpMethod.Get, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner));
        var groups = await list.Content.ReadFromJsonAsync<List<AccountGroupDto>>();
        Assert.Single(groups!.Where(group => group.Name == "New"));
    }

    [Fact]
    public async Task PlainMemberCanCreateGroup_NotOwnerGated()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        // s.Viewer is a plain space Member (not the space Owner) — group CRUD is member-gated, so this
        // must succeed. (Guards against accidentally tightening group CRUD to owner-only.)
        using var create = await client.SendAsync(Req(HttpMethod.Post, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Viewer, new { name = "By member" }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    [Fact]
    public async Task OutsiderCannotRenameOrDeleteGroup()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var group = await CreateGroupAsync(client, s, "Owned");

        using var rename = await client.SendAsync(Req(HttpMethod.Put, $"/api/account-groups/{group}?fullWorthSpaceId={s.Space}", s.Outsider, new { name = "Hijack" }));
        Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
        using var del = await client.SendAsync(Req(HttpMethod.Delete, $"/api/account-groups/{group}?fullWorthSpaceId={s.Space}", s.Outsider));
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    // ---- helpers ----

    private sealed record Scenario(Guid Space, Guid OtherSpace, Guid Owner, Guid Viewer, Guid Outsider, Guid Account, Guid OtherSpaceGroup);
    private sealed record AccountProbe(Guid Id, Guid? GroupId, string? GroupName);

    private static async Task<Guid> DefaultGroupIdAsync(HttpClient client, Scenario s)
    {
        using var r = await client.SendAsync(Req(HttpMethod.Get, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner));
        r.EnsureSuccessStatusCode();
        var groups = await r.Content.ReadFromJsonAsync<List<AccountGroupDto>>();
        return Assert.Single(groups!.Where(group => group.IsDefault)).Id;
    }

    private static async Task<Guid> CreateGroupAsync(HttpClient client, Scenario s, string name)
    {
        using var r = await client.SendAsync(Req(HttpMethod.Post, $"/api/account-groups?fullWorthSpaceId={s.Space}", s.Owner, new { name }));
        r.EnsureSuccessStatusCode();
        var dto = await r.Content.ReadFromJsonAsync<AccountGroupDto>();
        return dto!.Id;
    }

    private static async Task AssignAsync(HttpClient client, Scenario s, Guid user, Guid? group)
    {
        using var r = await client.SendAsync(Req(HttpMethod.Put, $"/api/accounts/{s.Account}/group?fullWorthSpaceId={s.Space}", user, new { groupId = group }));
        r.EnsureSuccessStatusCode();
    }

    private static async Task<AccountProbe> GetAccountAsync(HttpClient client, Scenario s, Guid user)
    {
        using var r = await client.SendAsync(Req(HttpMethod.Get, $"/api/accounts?fullWorthSpaceId={s.Space}", user));
        r.EnsureSuccessStatusCode();
        var accounts = await r.Content.ReadFromJsonAsync<List<AccountProbe>>();
        return accounts!.Single(a => a.Id == s.Account);
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            foreach (var id in new[] { s.Owner, s.Viewer, s.Outsider })
                db.Users.Add(new FullWorthUser { Id = id, EmailNormalized = $"{id:N}@EX.COM".ToUpperInvariant(), DisplayName = "G", IsActive = true });
            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = s.Space, Name = "Space", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = s.OtherSpace, Name = "Other", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Viewer, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = s.OtherSpace, UserId = s.Outsider, Role = FullWorthSpaceRoles.Owner });
            db.Accounts.Add(new FinanceAccount { Id = s.Account, FullWorthSpaceId = s.Space, Provider = "manual", IdentificationHash = $"g-{s.Account:N}", ProviderAccountId = $"g-{s.Account:N}", InstitutionName = "Cash", DisplayName = "Wallet", Currency = "EUR" });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = s.Account, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = s.Account, UserId = s.Viewer, OwnershipType = AccountOwnershipTypes.Viewer });
            db.AccountGroups.Add(new AccountGroup { Id = s.OtherSpaceGroup, FullWorthSpaceId = s.OtherSpace, Name = "Foreign" });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
