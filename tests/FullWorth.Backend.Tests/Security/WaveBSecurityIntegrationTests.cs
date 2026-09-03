using System.Net;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Security;

public sealed class WaveBSecurityIntegrationTests
{
    [Fact]
    public async Task SpaceAndAccountIdorSharedViewerThirdUserAndCrossSpaceOwnerAreEnforced()
    {
        using var factory = await StartAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var spaces = scope.ServiceProvider.GetRequiredService<FullWorthSpaceStore>();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();

        var userA = User("A@EXAMPLE.COM", "A");
        var userB = User("B@EXAMPLE.COM", "B");
        var userC = User("C@EXAMPLE.COM", "C");
        var userD = User("D@EXAMPLE.COM", "D");
        db.Users.AddRange(userA, userB, userC, userD);
        await db.SaveChangesAsync();

        var spaceA = await spaces.CreateAsync(userA.Id, "Space A", "EUR", CancellationToken.None);
        await spaces.AddMemberAsync(userA.Id, spaceA.Id, userB.Id, FullWorthSpaceRoles.Member, CancellationToken.None);
        await spaces.AddMemberAsync(userA.Id, spaceA.Id, userC.Id, FullWorthSpaceRoles.Member, CancellationToken.None);
        var spaceB = await spaces.CreateAsync(userD.Id, "Space B", "EUR", CancellationToken.None);

        Assert.Null(await spaces.GetForUserAsync(userB.Id, spaceB.Id, CancellationToken.None));

        var connection = new BankConnection
        {
            FullWorthSpaceId = spaceA.Id,
            InstitutionName = "Bank A",
            Country = "DE"
        };
        var account = new FinanceAccount
        {
            FullWorthSpaceId = spaceA.Id,
            BankConnectionId = connection.Id,
            Provider = "test",
            IdentificationHash = "private-a",
            ProviderAccountId = "private-a",
            InstitutionName = "Bank A",
            DisplayName = "Private A",
            Currency = "EUR"
        };
        db.AddRange(connection, account);
        await db.SaveChangesAsync();
        db.AccountOwners.AddRange(
            new AccountOwner { AccountId = account.Id, UserId = userA.Id, OwnershipType = AccountOwnershipTypes.Owner },
            new AccountOwner { AccountId = account.Id, UserId = userB.Id, OwnershipType = AccountOwnershipTypes.Viewer });
        await db.SaveChangesAsync();

        Assert.True(await accounts.CanUserAccessAsync(userA.Id, spaceA.Id, account.Id, CancellationToken.None));
        Assert.True(await accounts.CanUserEditAsync(userA.Id, spaceA.Id, account.Id, CancellationToken.None));
        Assert.True(await accounts.CanUserAccessAsync(userB.Id, spaceA.Id, account.Id, CancellationToken.None));
        Assert.False(await accounts.CanUserEditAsync(userB.Id, spaceA.Id, account.Id, CancellationToken.None));
        Assert.False(await accounts.CanUserAccessAsync(userC.Id, spaceA.Id, account.Id, CancellationToken.None));
        Assert.False(await accounts.CanUserAccessAsync(userD.Id, spaceA.Id, account.Id, CancellationToken.None));

        var crossSpace = await accounts.AddOwnerAsync(
            userA.Id, spaceA.Id, account.Id, userD.Id, AccountOwnershipTypes.Owner, CancellationToken.None);
        Assert.Equal(AccountOwnerChangeResult.TargetNotFullWorthSpaceMember, crossSpace);

        var lastOwner = await accounts.RemoveOwnerAsync(
            userA.Id, spaceA.Id, account.Id, userA.Id, CancellationToken.None);
        Assert.Equal(AccountOwnerChangeResult.LastOwner, lastOwner);

        var shared = await accounts.AddOwnerAsync(
            userA.Id, spaceA.Id, account.Id, userC.Id, AccountOwnershipTypes.Owner, CancellationToken.None);
        Assert.Equal(AccountOwnerChangeResult.Added, shared);
        Assert.True(await accounts.CanUserAccessAsync(userC.Id, spaceA.Id, account.Id, CancellationToken.None));
        Assert.True(await accounts.CanUserEditAsync(userC.Id, spaceA.Id, account.Id, CancellationToken.None));

        await spaces.RemoveMemberAsync(userA.Id, spaceA.Id, userB.Id, CancellationToken.None);
        // Removing a member is now a full de-provision: their per-account grants in the space are dropped,
        // so a later re-add cannot silently restore access (was previously left dangling).
        Assert.False(await db.AccountOwners.AnyAsync(x => x.AccountId == account.Id && x.UserId == userB.Id));
        Assert.False(await accounts.CanUserAccessAsync(userB.Id, spaceA.Id, account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task FullWorthSpaceServiceCreatesOwnerAndDefaultCategoriesAtomically()
    {
        using var factory = await StartAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<FullWorthSpaceService>();

        var user = User("NEWSPACE@EXAMPLE.COM", "New Space Owner");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var space = await service.CreateAsync(user.Id, "Household", "eur", CancellationToken.None);

        Assert.True(await db.FullWorthSpaceMembers.AnyAsync(x =>
            x.FullWorthSpaceId == space.Id && x.UserId == user.Id && x.Role == FullWorthSpaceRoles.Owner));
        Assert.Equal(FullWorthSeeder.DefaultCategoryCount, await db.Categories.CountAsync(x => x.FullWorthSpaceId == space.Id));
    }

    [Fact]
    public async Task FinalFullWorthSpaceOwnerCannotBeRemoved()
    {
        using var factory = await StartAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var spaces = scope.ServiceProvider.GetRequiredService<FullWorthSpaceStore>();

        var owner = User("LASTOWNER@EXAMPLE.COM", "Last Owner");
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var space = await spaces.CreateAsync(owner.Id, "Protected", "EUR", CancellationToken.None);

        await Assert.ThrowsAsync<FullWorthSpaceLastOwnerException>(() =>
            spaces.RemoveMemberAsync(owner.Id, space.Id, owner.Id, CancellationToken.None));
    }

    private static FullWorthUser User(string email, string name) => new()
    {
        EmailNormalized = email,
        DisplayName = name
    };

    private static async Task<BackendWebApplicationFactory> StartAsync()
    {
        var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return factory;
    }
}
