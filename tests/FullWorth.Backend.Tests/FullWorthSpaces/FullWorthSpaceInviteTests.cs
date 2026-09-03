using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.FullWorthSpaces;

// Multi-user sharing (UI_UX_SPEC): an owner issues an invite; the invitee claims it (internal seam) and
// gains scoped access via the pre-existing AccountOwner-based authorization. These lock the invite +
// membership provisioning and prove the granted access is correctly scoped (viewer read, no owner-only
// mutation, no un-shared data).
public sealed class FullWorthSpaceInviteTests
{
    [Fact]
    public async Task Owner_CreatesInvite_ListsWithoutToken_AndCanRevoke()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var created = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/invites", s.Owner,
            new { email = "new@ex.com", role = "member", accounts = new[] { new { accountId = s.Account, ownershipType = "viewer" } } }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("claimToken").GetString();
        var inviteId = body.GetProperty("inviteId").GetGuid();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var list = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/fullworth-spaces/{s.Space}/invites", s.Owner));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var invites = await list.Content.ReadFromJsonAsync<JsonElement>();
        var row = invites.EnumerateArray().Single();
        Assert.Equal(inviteId, row.GetProperty("id").GetGuid());
        Assert.False(row.TryGetProperty("claimToken", out _));   // token is never listed
        Assert.False(row.TryGetProperty("tokenHash", out _));

        var revoke = await client.SendAsync(UserRequest(HttpMethod.Delete, $"/api/fullworth-spaces/{s.Space}/invites/{inviteId}", s.Owner));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
    }

    [Fact]
    public async Task NonOwnerMemberGets403_AndOutsiderGets404_OnCreate()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var payload = new { email = "x@ex.com", role = "member", accounts = Array.Empty<object>() };
        var byMember = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/invites", s.PlainMember, payload));
        var byOutsider = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/invites", s.Outsider, payload));

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOutsider.StatusCode);
    }

    [Fact]
    public async Task SharingAnAccountTheOwnerDoesNotOwnIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        // s.PlainMemberAccount is owned by PlainMember, not by Owner → invalid grant.
        var created = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/invites", s.Owner,
            new { email = "new@ex.com", role = "member", accounts = new[] { new { accountId = s.PlainMemberAccount, ownershipType = "viewer" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task Accept_GrantsScopedAccess_ButNotOwnerMutations_NorUnsharedData()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var created = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/invites", s.Owner,
            new { email = "invitee@ex.com", role = "member", accounts = new[] { new { accountId = s.Account, ownershipType = "viewer" } } }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("claimToken").GetString();

        // Accept via the internal seam (no user context).
        var accept = await client.SendAsync(InternalRequest(HttpMethod.Post, "/api/bootstrap/accept-invite", new { token }));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var invitee = (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("financeUserId").GetGuid();
        Assert.NotEqual(Guid.Empty, invitee);

        // Viewer can READ the shared account + analytics.
        var readShared = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/accounts/{s.Account}?fullWorthSpaceId={s.Space}", invitee));
        var overview = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/analytics/overview?fullWorthSpaceId={s.Space}", invitee));
        Assert.Equal(HttpStatusCode.OK, readShared.StatusCode);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);

        // Viewer CANNOT mutate the account (owner-only) and CANNOT see an un-shared account.
        var patch = await client.SendAsync(UserRequest(HttpMethod.Patch, $"/api/accounts/{s.Account}?fullWorthSpaceId={s.Space}", invitee,
            new AccountSettingsRequest("hijack", null, null, null)));
        var unshared = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/accounts/{s.PlainMemberAccount}?fullWorthSpaceId={s.Space}", invitee));
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unshared.StatusCode);

        // The invite is now claimed: a second accept with the same token fails.
        var replay = await client.SendAsync(InternalRequest(HttpMethod.Post, "/api/bootstrap/accept-invite", new { token }));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task RevokedInvite_CannotBeAccepted()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var created = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/invites", s.Owner,
            new { email = "revoked@ex.com", role = "member", accounts = Array.Empty<object>() }));
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = createdBody.GetProperty("claimToken").GetString();
        var inviteId = createdBody.GetProperty("inviteId").GetGuid();

        await client.SendAsync(UserRequest(HttpMethod.Delete, $"/api/fullworth-spaces/{s.Space}/invites/{inviteId}", s.Owner));

        var accept = await client.SendAsync(InternalRequest(HttpMethod.Post, "/api/bootstrap/accept-invite", new { token }));
        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);
    }

    [Fact]
    public async Task AddMemberByEmail_UnknownEmail404_AlreadyMember409()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var unknown = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/members", s.Owner,
            new { email = "nobody@ex.com", role = "member" }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // PlainMember already belongs to the space.
        var dup = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/members", s.Owner,
            new { email = PlainMemberEmail, role = "member" }));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task MembersList_IsReadableByAnyMember_AndExposesOnlySafeFields()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var list = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/fullworth-spaces/{s.Space}/members", s.PlainMember));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var rows = (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.True(first.TryGetProperty("userId", out _));
        Assert.True(first.TryGetProperty("displayName", out _));
        Assert.True(first.TryGetProperty("email", out _));
        Assert.True(first.TryGetProperty("role", out _));
        Assert.False(first.TryGetProperty("isActive", out _));   // no sensitive user fields leak

        var outsider = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/fullworth-spaces/{s.Space}/members", s.Outsider));
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    // Removing a member must fully de-provision: their per-account grants in the space are dropped, so a
    // later re-add that does NOT re-share the account cannot silently restore access.
    [Fact]
    public async Task RemovingMember_RevokesAccountGrants_SoReAddDoesNotRestoreAccess()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        // PlainMember starts with viewer access to the owner's account.
        var before = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/accounts/{s.Account}?fullWorthSpaceId={s.Space}", s.PlainMember));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Owner removes then re-adds PlainMember (the re-add shares no accounts).
        var removed = await client.SendAsync(UserRequest(HttpMethod.Delete, $"/api/fullworth-spaces/{s.Space}/members/{s.PlainMember}", s.Owner));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var readded = await client.SendAsync(UserRequest(HttpMethod.Post, $"/api/fullworth-spaces/{s.Space}/members", s.Owner,
            new { email = PlainMemberEmail, role = "member" }));
        Assert.Equal(HttpStatusCode.OK, readded.StatusCode);

        // The stale grant was dropped on removal → re-added member can no longer read the account.
        var after = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/accounts/{s.Account}?fullWorthSpaceId={s.Space}", s.PlainMember));
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    // ---- helpers ----

    private const string PlainMemberEmail = "member@ex.com";
    private sealed record Scenario(Guid Space, Guid Owner, Guid PlainMember, Guid Outsider, Guid Connection, Guid Account, Guid PlainMemberAccount);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedFullWorthUserAsync(s.Outsider);
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = s.Owner, EmailNormalized = "OWNER@EX.COM", DisplayName = "Owner", IsActive = true });
            db.Users.Add(new FullWorthUser { Id = s.PlainMember, EmailNormalized = PlainMemberEmail.ToUpperInvariant(), DisplayName = "Member", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Household", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.PlainMember, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection { Id = s.Connection, FullWorthSpaceId = s.Space, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"inv-{s.Connection:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = s.Account, FullWorthSpaceId = s.Space, BankConnectionId = s.Connection, Provider = "test", IdentificationHash = $"inv-{s.Account:N}", ProviderAccountId = $"inv-{s.Account:N}", InstitutionName = "Bank", DisplayName = "Owner Acc", Currency = "EUR" });
            db.Accounts.Add(new FinanceAccount { Id = s.PlainMemberAccount, FullWorthSpaceId = s.Space, BankConnectionId = s.Connection, Provider = "test", IdentificationHash = $"inv-{s.PlainMemberAccount:N}", ProviderAccountId = $"inv-{s.PlainMemberAccount:N}", InstitutionName = "Bank", DisplayName = "Member Acc", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = s.Account, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.AccountOwners.Add(new AccountOwner { AccountId = s.PlainMemberAccount, UserId = s.PlainMember, OwnershipType = AccountOwnershipTypes.Owner });
            // PlainMember also has a viewer grant on the owner's account (used by the revocation test).
            db.AccountOwners.Add(new AccountOwner { AccountId = s.Account, UserId = s.PlainMember, OwnershipType = AccountOwnershipTypes.Viewer });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage InternalRequest(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
