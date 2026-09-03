using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.FullWorthSpaces;

public sealed class FullWorthSpaceStoreTests
{
    private static readonly Guid OwnerUserId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid MemberUserId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid ThirdUserId = Guid.Parse("30000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task OwnerCreatesFullWorthSpace()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);

        var space = await store.CreateAsync(OwnerUserId, " Household ", "EUR", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, space.Id);
        Assert.Equal("Household", space.Name);
        Assert.Equal(TimeSpan.Zero, space.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, space.UpdatedAt.Offset);
        Assert.Equal(1, await db.FullWorthSpaces.CountAsync());
    }

    [Fact]
    public async Task CreatorBecomesOwnerAutomatically()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);

        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);
        var membership = await db.FullWorthSpaceMembers.SingleAsync();

        Assert.Equal(space.Id, membership.FullWorthSpaceId);
        Assert.Equal(OwnerUserId, membership.UserId);
        Assert.Equal(FullWorthSpaceRoles.Owner, membership.Role);
        Assert.Equal(TimeSpan.Zero, membership.JoinedAt.Offset);
    }

    [Fact]
    public async Task UserListsOnlyOwnAndMemberSpaces()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);

        var ownerSpace = await store.CreateAsync(OwnerUserId, "Owner space", null, CancellationToken.None);
        var memberSpace = await store.CreateAsync(MemberUserId, "Member space", null, CancellationToken.None);
        await store.AddMemberAsync(MemberUserId, memberSpace.Id, OwnerUserId, FullWorthSpaceRoles.Member, CancellationToken.None);
        await store.CreateAsync(ThirdUserId, "Third space", null, CancellationToken.None);

        var spaces = await store.ListForUserAsync(OwnerUserId, CancellationToken.None);

        Assert.Equal(2, spaces.Count);
        Assert.Contains(spaces, item => item.Id == ownerSpace.Id);
        Assert.Contains(spaces, item => item.Id == memberSpace.Id);
        Assert.DoesNotContain(spaces, item => item.Name == "Third space");
    }

    [Fact]
    public async Task OwnerCanRetrieveOwnFullWorthSpace()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);

        var result = await store.GetForUserAsync(OwnerUserId, space.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(space.Id, result.Id);
    }

    [Fact]
    public async Task MemberCanRetrieveJoinedFullWorthSpace()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);
        await store.AddMemberAsync(OwnerUserId, space.Id, MemberUserId, FullWorthSpaceRoles.Member, CancellationToken.None);

        var result = await store.GetForUserAsync(MemberUserId, space.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(space.Id, result.Id);
    }

    [Fact]
    public async Task ThirdUserCannotRetrieveFullWorthSpace()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);

        var result = await store.GetForUserAsync(ThirdUserId, space.Id, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GuessedUuidDoesNotRevealExistence()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);
        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);
        var missingSpaceId = Guid.Parse("40000000-0000-0000-0000-000000000004");

        var unauthorizedExisting = await store.GetForUserAsync(ThirdUserId, space.Id, CancellationToken.None);
        var nonexistent = await store.GetForUserAsync(ThirdUserId, missingSpaceId, CancellationToken.None);

        Assert.Null(unauthorizedExisting);
        Assert.Null(nonexistent);
    }

    [Fact]
    public async Task DefaultBaseCurrencyIsEur()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);

        var space = await store.CreateAsync(OwnerUserId, "Household", null, CancellationToken.None);

        Assert.Equal("EUR", space.BaseCurrency);
    }

    [Fact]
    public async Task CustomSupportedCurrencyCanBeStored()
    {
        await using var database = await FullWorthSpaceTestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new FullWorthSpaceStore(db);

        var space = await store.CreateAsync(OwnerUserId, "US household", "usd", CancellationToken.None);

        Assert.Equal("USD", space.BaseCurrency);
        Assert.Equal("USD", (await db.FullWorthSpaces.SingleAsync()).BaseCurrency);
    }
}
