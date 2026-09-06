using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Users;

public sealed class AccountPurgeTests
{
    [Fact]
    public async Task PurgeManifest_ClassifiesEveryCurrentFinanceEntity()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        var unclassified = PersonalDataPurgeManifest.Unclassified(db.Model);

        Assert.True(
            unclassified.Count == 0,
            "Unclassified purge entities: " +
            string.Join(", ", unclassified.Select(x => x.EntityType.Name)));
    }

    [Fact]
    public async Task PurgeSoleMemberSpace_RemovesSpaceDataAndLeavesUniqueTombstone()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        Guid userId;
        Guid spaceId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserStore>();
            var spaces = scope.ServiceProvider.GetRequiredService<FullWorthSpaceService>();
            var user = await users.CreateAsync(
                new CreateUserRequest("purge-owner@example.com", "Purge Owner"),
                CancellationToken.None);
            var space = await spaces.CreateAsync(user.Id, "Delete Me", "EUR", CancellationToken.None);
            userId = user.Id;
            spaceId = space.Id;

            // Default categories seeded by FullWorthSpaceService include real space-owned dependencies,
            // including hierarchical categories. The purge therefore exercises the FK ordering and
            // nullable self-reference preparation instead of deleting an empty shell.
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
            Assert.True(await db.Categories.AnyAsync(x => x.FullWorthSpaceId == spaceId));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var purge = scope.ServiceProvider.GetRequiredService<AccountPurgeService>();
            var result = await purge.PurgeAsync(userId, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error);
            Assert.False(result.AlreadyPurged);
            Assert.Equal(1, result.PersonalSpacesPurged);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
            var deleted = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId);

            Assert.True(deleted.IsTombstone);
            Assert.False(deleted.IsActive);
            Assert.Equal("Deleted user", deleted.DisplayName);
            Assert.StartsWith($"DELETED-{userId:N}".ToUpperInvariant(), deleted.EmailNormalized);
            Assert.False(await db.FullWorthSpaces.AnyAsync(x => x.Id == spaceId));
            Assert.False(await db.FullWorthSpaceMembers.AnyAsync(x => x.FullWorthSpaceId == spaceId));
            Assert.False(await db.Categories.AnyAsync(x => x.FullWorthSpaceId == spaceId));
        }
    }

    [Fact]
    public async Task PurgeSharedSpace_PreservesSharedFinanceAndTransfersLastOwnership()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        Guid deletingUserId;
        Guid remainingUserId;
        Guid spaceId;
        Guid accountId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserStore>();
            var spaces = scope.ServiceProvider.GetRequiredService<FullWorthSpaceService>();
            var spaceStore = scope.ServiceProvider.GetRequiredService<FullWorthSpaceStore>();
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

            var deleting = await users.CreateAsync(
                new CreateUserRequest("shared-owner@example.com", "Shared Owner"),
                CancellationToken.None);
            var remaining = await users.CreateAsync(
                new CreateUserRequest("shared-member@example.com", "Shared Member"),
                CancellationToken.None);
            var space = await spaces.CreateAsync(deleting.Id, "Shared", "EUR", CancellationToken.None);
            await spaceStore.AddMemberAsync(
                deleting.Id,
                space.Id,
                remaining.Id,
                FullWorthSpaceRoles.Member,
                CancellationToken.None);

            var account = new FinanceAccount
            {
                FullWorthSpaceId = space.Id,
                Provider = "manual",
                IdentificationHash = $"manual:{Guid.NewGuid():N}",
                ProviderAccountId = $"manual:{Guid.NewGuid():N}",
                InstitutionName = "Shared Bank",
                DisplayName = "Shared Account",
                Currency = "EUR"
            };
            account.Owners.Add(new AccountOwner
            {
                Account = account,
                UserId = deleting.Id,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            deletingUserId = deleting.Id;
            remainingUserId = remaining.Id;
            spaceId = space.Id;
            accountId = account.Id;
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var purge = scope.ServiceProvider.GetRequiredService<AccountPurgeService>();
            var result = await purge.PurgeAsync(deletingUserId, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(0, result.PersonalSpacesPurged);
            Assert.Equal(1, result.SharedSpacesLeft);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

            Assert.True(await db.FullWorthSpaces.AnyAsync(x => x.Id == spaceId));
            Assert.True(await db.Accounts.AnyAsync(x => x.Id == accountId));
            Assert.False(await db.FullWorthSpaceMembers.AnyAsync(x =>
                x.FullWorthSpaceId == spaceId && x.UserId == deletingUserId));

            var remainingMember = await db.FullWorthSpaceMembers.AsNoTracking()
                .SingleAsync(x => x.FullWorthSpaceId == spaceId && x.UserId == remainingUserId);
            Assert.Equal(FullWorthSpaceRoles.Owner, remainingMember.Role);

            var remainingOwner = await db.AccountOwners.AsNoTracking()
                .SingleAsync(x => x.AccountId == accountId && x.UserId == remainingUserId);
            Assert.Equal(AccountOwnershipTypes.Owner, remainingOwner.OwnershipType);
            Assert.False(await db.AccountOwners.AnyAsync(x =>
                x.AccountId == accountId && x.UserId == deletingUserId));

            var tombstone = await db.Users.AsNoTracking().SingleAsync(x => x.Id == deletingUserId);
            Assert.True(tombstone.IsTombstone);
            Assert.False(tombstone.IsActive);

            var survivor = await db.Users.AsNoTracking().SingleAsync(x => x.Id == remainingUserId);
            Assert.False(survivor.IsTombstone);
            Assert.True(survivor.IsActive);
        }
    }

    [Fact]
    public async Task ScheduledBankingList_ExcludesInactiveAndTombstoneAuthorizers()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserStore>();
        var spaces = scope.ServiceProvider.GetRequiredService<FullWorthSpaceService>();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        var active = await users.CreateAsync(
            new CreateUserRequest("bank-active@example.com", "Active"),
            CancellationToken.None);
        var inactive = await users.CreateAsync(
            new CreateUserRequest("bank-inactive@example.com", "Inactive"),
            CancellationToken.None);
        var tombstone = await users.CreateAsync(
            new CreateUserRequest("bank-deleted@example.com", "Deleted"),
            CancellationToken.None);

        var activeSpace = await spaces.CreateAsync(active.Id, "Active Bank", "EUR", CancellationToken.None);
        var inactiveSpace = await spaces.CreateAsync(inactive.Id, "Inactive Bank", "EUR", CancellationToken.None);
        var tombstoneSpace = await spaces.CreateAsync(tombstone.Id, "Deleted Bank", "EUR", CancellationToken.None);

        await users.SetActiveAsync(inactive.Id, false, CancellationToken.None);
        await users.TombstoneAsync(tombstone.Id, CancellationToken.None);

        db.BankConnections.AddRange(
            new BankConnection
            {
                FullWorthSpaceId = activeSpace.Id,
                AuthorizationUserId = active.Id,
                Provider = "enable-banking",
                InstitutionName = "Active",
                Status = "READY"
            },
            new BankConnection
            {
                FullWorthSpaceId = inactiveSpace.Id,
                AuthorizationUserId = inactive.Id,
                Provider = "enable-banking",
                InstitutionName = "Inactive",
                Status = "READY"
            },
            new BankConnection
            {
                FullWorthSpaceId = tombstoneSpace.Id,
                AuthorizationUserId = tombstone.Id,
                Provider = "enable-banking",
                InstitutionName = "Deleted",
                Status = "READY"
            });
        await db.SaveChangesAsync();

        var store = scope.ServiceProvider.GetRequiredService<BankConnectionStore>();
        var listed = await store.ListAsync(CancellationToken.None);

        Assert.Contains(listed, x => x.AuthorizationUserId == active.Id);
        Assert.DoesNotContain(listed, x => x.AuthorizationUserId == inactive.Id);
        Assert.DoesNotContain(listed, x => x.AuthorizationUserId == tombstone.Id);
    }
}
