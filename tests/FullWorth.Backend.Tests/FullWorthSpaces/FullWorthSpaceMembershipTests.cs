using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.FullWorthSpaces;

public sealed class FullWorthSpaceMembershipTests
{
    private static readonly Guid OwnerUserId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid MemberUserId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherUserId = Guid.Parse("30000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task OwnerCanAddMember()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);

        var membership = await store.AddMemberAsync(
            OwnerUserId,
            space.Id,
            MemberUserId,
            FullWorthSpaceRoles.Member,
            CancellationToken.None);

        Assert.Equal(MemberUserId, membership.UserId);
        Assert.Equal(FullWorthSpaceRoles.Member, membership.Role);
        Assert.True(await store.IsMemberAsync(MemberUserId, space.Id, CancellationToken.None));
    }

    [Fact]
    public async Task NonOwnerCannotAddMember()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);
        await store.AddMemberAsync(OwnerUserId, space.Id, MemberUserId, FullWorthSpaceRoles.Member, CancellationToken.None);

        await Assert.ThrowsAsync<FullWorthSpaceNotFoundException>(() => store.AddMemberAsync(
            MemberUserId,
            space.Id,
            OtherUserId,
            FullWorthSpaceRoles.Member,
            CancellationToken.None));

        Assert.False(await store.IsMemberAsync(OtherUserId, space.Id, CancellationToken.None));
    }

    [Fact]
    public async Task OwnerCanRemoveNormalMember()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);
        await store.AddMemberAsync(OwnerUserId, space.Id, MemberUserId, FullWorthSpaceRoles.Member, CancellationToken.None);

        await store.RemoveMemberAsync(OwnerUserId, space.Id, MemberUserId, CancellationToken.None);

        Assert.False(await store.IsMemberAsync(MemberUserId, space.Id, CancellationToken.None));
        Assert.True(await store.IsMemberAsync(OwnerUserId, space.Id, CancellationToken.None));
    }

    [Fact]
    public async Task NonOwnerCannotRemoveMemberOrOwner()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);
        await store.AddMemberAsync(OwnerUserId, space.Id, MemberUserId, FullWorthSpaceRoles.Member, CancellationToken.None);
        await store.AddMemberAsync(OwnerUserId, space.Id, OtherUserId, FullWorthSpaceRoles.Member, CancellationToken.None);

        await Assert.ThrowsAsync<FullWorthSpaceNotFoundException>(() => store.RemoveMemberAsync(
            MemberUserId,
            space.Id,
            OtherUserId,
            CancellationToken.None));
        await Assert.ThrowsAsync<FullWorthSpaceNotFoundException>(() => store.RemoveMemberAsync(
            MemberUserId,
            space.Id,
            OwnerUserId,
            CancellationToken.None));

        Assert.True(await store.IsMemberAsync(OtherUserId, space.Id, CancellationToken.None));
        Assert.True(await store.IsMemberAsync(OwnerUserId, space.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateMembershipIsPrevented()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);
        await store.AddMemberAsync(OwnerUserId, space.Id, MemberUserId, FullWorthSpaceRoles.Member, CancellationToken.None);

        await Assert.ThrowsAsync<FullWorthSpaceMembershipExistsException>(() => store.AddMemberAsync(
            OwnerUserId,
            space.Id,
            MemberUserId,
            FullWorthSpaceRoles.Member,
            CancellationToken.None));

        Assert.Equal(2, await db.FullWorthSpaceMembers.CountAsync());
    }

    [Fact]
    public async Task InvalidRoleIsRejected()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddMemberAsync(
            OwnerUserId,
            space.Id,
            MemberUserId,
            "admin",
            CancellationToken.None));

        Assert.False(await store.IsMemberAsync(MemberUserId, space.Id, CancellationToken.None));
    }

    [Fact]
    public async Task LastOwnerCannotBeRemoved()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);

        await Assert.ThrowsAsync<FullWorthSpaceLastOwnerException>(() => store.RemoveMemberAsync(
            OwnerUserId,
            space.Id,
            OwnerUserId,
            CancellationToken.None));

        Assert.True(await store.IsMemberAsync(OwnerUserId, space.Id, CancellationToken.None));
    }
}
