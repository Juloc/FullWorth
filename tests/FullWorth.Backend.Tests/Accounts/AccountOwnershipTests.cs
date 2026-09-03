using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Ingestion;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Accounts;

public sealed class AccountOwnershipTests
{
    [Fact]
    public async Task PersonalAccountHasOneOwner()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (userId, AccountOwnershipTypes.Owner));

        var owners = await new AccountStore(db).ListOwnersAsync(account.Id, spaceId, CancellationToken.None);

        var owner = Assert.Single(owners);
        Assert.Equal(userId, owner.UserId);
        Assert.Equal(AccountOwnershipTypes.Owner, owner.OwnershipType);
    }

    [Fact]
    public async Task SharedAccountSupportsTwoOwners()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId,
            (first, AccountOwnershipTypes.Owner),
            (second, AccountOwnershipTypes.Owner));

        var owners = await new AccountStore(db).ListOwnersAsync(account.Id, spaceId, CancellationToken.None);

        Assert.Equal(2, owners.Count);
        Assert.All(owners, x => Assert.Equal(AccountOwnershipTypes.Owner, x.OwnershipType));
    }

    [Fact]
    public async Task SharedAccountSupportsOwnerAndViewer()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId,
            (ownerId, AccountOwnershipTypes.Owner),
            (viewerId, AccountOwnershipTypes.Viewer));

        var owners = await new AccountStore(db).ListOwnersAsync(account.Id, spaceId, CancellationToken.None);

        Assert.Contains(owners, x => x.UserId == ownerId && x.OwnershipType == AccountOwnershipTypes.Owner);
        Assert.Contains(owners, x => x.UserId == viewerId && x.OwnershipType == AccountOwnershipTypes.Viewer);
    }

    [Fact]
    public async Task OwnerCanAccessAccount()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceId));

        Assert.True(await service.CanUserAccessAsync(ownerId, spaceId, account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ViewerCanAccessAccount()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (viewerId, AccountOwnershipTypes.Viewer));
        var service = CreateService(db, (viewerId, spaceId));

        Assert.True(await service.CanUserAccessAsync(viewerId, spaceId, account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ViewerCannotEditAccount()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (viewerId, AccountOwnershipTypes.Viewer));
        var service = CreateService(db, (viewerId, spaceId));

        Assert.False(await service.CanUserEditAsync(viewerId, spaceId, account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ThirdUserIsDenied()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var thirdUserId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceId), (thirdUserId, spaceId));

        Assert.False(await service.CanUserAccessAsync(thirdUserId, spaceId, account.Id, CancellationToken.None));
        Assert.False(await service.CanUserEditAsync(thirdUserId, spaceId, account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GuessedAccountUuidDoesNotGrantAccess()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await AddAccountAsync(db, spaceId, (userA, AccountOwnershipTypes.Owner));
        var privateAccount = await AddAccountAsync(db, spaceId, (userB, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (userA, spaceId), (userB, spaceId));

        Assert.False(await service.CanUserAccessAsync(userA, spaceId, privateAccount.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateAccountOwnerIsRejectedByDatabaseKey()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (userId, AccountOwnershipTypes.Owner));
        db.ChangeTracker.Clear();
        db.Set<AccountOwner>().Add(new AccountOwner
        {
            AccountId = account.Id,
            UserId = userId,
            OwnershipType = AccountOwnershipTypes.Owner
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task InvalidOwnershipTypeIsRejected()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceId), (targetId, spaceId));

        var result = await service.AddOwnerAsync(ownerId, spaceId, account.Id, targetId, "editor", CancellationToken.None);

        Assert.Equal(AccountOwnerChangeResult.InvalidOwnershipType, result);
    }

    [Fact]
    public async Task CrossFullWorthSpaceAssignmentIsRejectedByMembershipContract()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceA = Guid.NewGuid();
        var spaceB = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceA, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceA), (targetId, spaceB));

        var result = await service.AddOwnerAsync(ownerId, spaceA, account.Id, targetId, AccountOwnershipTypes.Owner, CancellationToken.None);

        Assert.Equal(AccountOwnerChangeResult.TargetNotFullWorthSpaceMember, result);
        Assert.False(await db.Set<AccountOwner>().AnyAsync(x => x.AccountId == account.Id && x.UserId == targetId));
    }

    [Fact]
    public async Task OwnerCanAddAnotherOwner()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceId), (targetId, spaceId));

        var result = await service.AddOwnerAsync(ownerId, spaceId, account.Id, targetId, AccountOwnershipTypes.Owner, CancellationToken.None);

        Assert.Equal(AccountOwnerChangeResult.Added, result);
        Assert.True(await db.Set<AccountOwner>().AnyAsync(x => x.AccountId == account.Id && x.UserId == targetId && x.OwnershipType == AccountOwnershipTypes.Owner));
    }

    [Fact]
    public async Task OwnerCanAddViewer()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceId), (viewerId, spaceId));

        var result = await service.AddOwnerAsync(ownerId, spaceId, account.Id, viewerId, AccountOwnershipTypes.Viewer, CancellationToken.None);

        Assert.Equal(AccountOwnerChangeResult.Added, result);
        Assert.True(await db.Set<AccountOwner>().AnyAsync(x => x.AccountId == account.Id && x.UserId == viewerId && x.OwnershipType == AccountOwnershipTypes.Viewer));
    }

    [Fact]
    public async Task ViewerCannotAddOwner()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId,
            (ownerId, AccountOwnershipTypes.Owner),
            (viewerId, AccountOwnershipTypes.Viewer));
        var service = CreateService(db, (ownerId, spaceId), (viewerId, spaceId), (targetId, spaceId));

        var result = await service.AddOwnerAsync(viewerId, spaceId, account.Id, targetId, AccountOwnershipTypes.Owner, CancellationToken.None);

        Assert.Equal(AccountOwnerChangeResult.AccessDenied, result);
    }

    [Fact]
    public async Task ViewerCannotRemoveOwner()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId,
            (ownerId, AccountOwnershipTypes.Owner),
            (viewerId, AccountOwnershipTypes.Viewer));
        var service = CreateService(db, (ownerId, spaceId), (viewerId, spaceId));

        var result = await service.RemoveOwnerAsync(viewerId, spaceId, account.Id, ownerId, CancellationToken.None);

        Assert.Equal(AccountOwnerChangeResult.AccessDenied, result);
        Assert.True(await db.Set<AccountOwner>().AnyAsync(x => x.AccountId == account.Id && x.UserId == ownerId));
    }

    [Fact]
    public async Task LastOwnerCannotBeRemoved()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId,
            (ownerId, AccountOwnershipTypes.Owner),
            (viewerId, AccountOwnershipTypes.Viewer));
        var service = CreateService(db, (ownerId, spaceId), (viewerId, spaceId));

        var result = await service.RemoveOwnerAsync(ownerId, spaceId, account.Id, ownerId, CancellationToken.None);

        Assert.Equal(AccountOwnerChangeResult.LastOwner, result);
        Assert.True(await db.Set<AccountOwner>().AnyAsync(x => x.AccountId == account.Id && x.UserId == ownerId));
    }

    [Fact]
    public async Task OwnerListRequiresAccountAccess()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var thirdUserId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceId), (thirdUserId, spaceId));

        var visible = await service.ListOwnersAsync(ownerId, spaceId, account.Id, CancellationToken.None);
        var hidden = await service.ListOwnersAsync(thirdUserId, spaceId, account.Id, CancellationToken.None);

        Assert.NotNull(visible);
        Assert.Null(hidden);
    }

    [Fact]
    public async Task OwnershipChangesDoNotDeleteAccount()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceId), (viewerId, spaceId));

        Assert.Equal(AccountOwnerChangeResult.Added,
            await service.AddOwnerAsync(ownerId, spaceId, account.Id, viewerId, AccountOwnershipTypes.Viewer, CancellationToken.None));
        Assert.Equal(AccountOwnerChangeResult.Removed,
            await service.RemoveOwnerAsync(ownerId, spaceId, account.Id, viewerId, CancellationToken.None));

        Assert.True(await db.Accounts.AnyAsync(x => x.Id == account.Id));
    }

    [Fact]
    public async Task RemovingUserAccessDoesNotDeleteBalancesOrTransactions()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId,
            (ownerId, AccountOwnershipTypes.Owner),
            (viewerId, AccountOwnershipTypes.Viewer));
        db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = account.Id, Amount = 100m, Currency = "EUR", BalanceType = "closingBooked" });
        db.Transactions.Add(new FinanceTransaction { AccountId = account.Id, ExternalKey = "ownership-test", Amount = -5m, Currency = "EUR" });
        await db.SaveChangesAsync();
        var service = CreateService(db, (ownerId, spaceId), (viewerId, spaceId));

        var result = await service.RemoveOwnerAsync(ownerId, spaceId, account.Id, viewerId, CancellationToken.None);

        Assert.Equal(AccountOwnerChangeResult.Removed, result);
        Assert.True(await db.BalanceSnapshots.AnyAsync(x => x.AccountId == account.Id));
        Assert.True(await db.Transactions.AnyAsync(x => x.AccountId == account.Id));
    }

    [Fact]
    public async Task IdentificationHashReconciliationRemainsProviderScoped()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var service = new IngestionService(db);

        await service.IngestAsync(CreateAccountBatch("enable-banking", "session-a", "stable-hash", "provider-account-1", "Old name"), CancellationToken.None);
        await service.IngestAsync(CreateAccountBatch("enable-banking", "session-a", "stable-hash", "provider-account-2", "Updated name"), CancellationToken.None);
        await service.IngestAsync(CreateAccountBatch("other-provider", "session-b", "stable-hash", "provider-account-3", "Other provider"), CancellationToken.None);

        var accounts = await db.Accounts.AsNoTracking().ToListAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Single(accounts.Where(x => x.Provider == "enable-banking"));
        Assert.Equal("provider-account-2", accounts.Single(x => x.Provider == "enable-banking").ProviderAccountId);
    }

    [Fact]
    public async Task FullWorthSpaceMemberWithoutAccountOwnerIsDenied()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (ownerId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (ownerId, spaceId), (memberId, spaceId));

        Assert.False(await service.CanUserAccessAsync(memberId, spaceId, account.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AccountOwnerOutsideFullWorthSpaceIsDenied()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var spaceId = Guid.NewGuid();
        var otherSpaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var account = await AddAccountAsync(db, spaceId, (userId, AccountOwnershipTypes.Owner));
        var service = CreateService(db, (userId, otherSpaceId));

        Assert.False(await service.CanUserAccessAsync(userId, spaceId, account.Id, CancellationToken.None));
        Assert.False(await service.CanUserEditAsync(userId, spaceId, account.Id, CancellationToken.None));
    }

    private static AccountService CreateService(
        FullWorth.Backend.Data.FullWorthDbContext db,
        params (Guid UserId, Guid FullWorthSpaceId)[] memberships)
    {
        foreach (var membership in memberships.Distinct())
        {
            if (!db.FullWorthSpaces.Any(x => x.Id == membership.FullWorthSpaceId))
                db.FullWorthSpaces.Add(new FullWorthSpace { Id = membership.FullWorthSpaceId, Name = "Test Space", BaseCurrency = "EUR" });
            if (!db.Users.Any(x => x.Id == membership.UserId))
                db.Users.Add(new FullWorthUser { Id = membership.UserId, EmailNormalized = $"{membership.UserId:N}@TEST.INVALID".ToUpperInvariant(), DisplayName = "Test User" });
            if (!db.FullWorthSpaceMembers.Any(x => x.FullWorthSpaceId == membership.FullWorthSpaceId && x.UserId == membership.UserId))
                db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = membership.FullWorthSpaceId, UserId = membership.UserId, Role = FullWorthSpaceRoles.Member });
        }
        db.SaveChanges();
        return new(new AccountStore(db), new FakeFullWorthSpaceMembership(memberships));
    }

    private static async Task<FinanceAccount> AddAccountAsync(
        FullWorth.Backend.Data.FullWorthDbContext db,
        Guid fullWorthSpaceId,
        params (Guid UserId, string OwnershipType)[] owners)
    {
        if (!await db.FullWorthSpaces.AnyAsync(x => x.Id == fullWorthSpaceId))
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = fullWorthSpaceId, Name = "Test Space", BaseCurrency = "EUR" });
        foreach (var owner in owners)
        {
            if (!await db.Users.AnyAsync(x => x.Id == owner.UserId))
                db.Users.Add(new FullWorthUser { Id = owner.UserId, EmailNormalized = $"{owner.UserId:N}@TEST.INVALID".ToUpperInvariant(), DisplayName = "Test User" });
        }

        var connection = new BankConnection
        {
            FullWorthSpaceId = fullWorthSpaceId,
            Provider = "test",
            InstitutionName = "Test Bank",
            Country = "DE",
            ProviderSessionId = Guid.NewGuid().ToString("N")
        };
        var account = new FinanceAccount
        {
            FullWorthSpaceId = fullWorthSpaceId,
            BankConnectionId = connection.Id,
            Provider = "test",
            IdentificationHash = Guid.NewGuid().ToString("N"),
            ProviderAccountId = Guid.NewGuid().ToString("N"),
            InstitutionName = "Test Bank",
            DisplayName = "Test account"
        };
        db.BankConnections.Add(connection);
        db.Accounts.Add(account);
        foreach (var owner in owners)
        {
            db.Set<AccountOwner>().Add(new AccountOwner
            {
                AccountId = account.Id,
                UserId = owner.UserId,
                OwnershipType = owner.OwnershipType
            });
        }
        await db.SaveChangesAsync();
        return account;
    }

    private static FinanceIngestBatch CreateAccountBatch(
        string provider,
        string sessionId,
        string identificationHash,
        string providerAccountId,
        string displayName) =>
        new(
            new BankConnectionBatch(null, provider, "Test Bank", "DE", sessionId, "AUTHORIZED", null, new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero), null, FullWorthSpaceDefaults.LegacyId),
            [new AccountBatchItem(identificationHash, providerAccountId, "Test Bank", displayName, "Current", "checking", "EUR", "1234", true)],
            [],
            []);

    private sealed class FakeFullWorthSpaceMembership(IEnumerable<(Guid UserId, Guid FullWorthSpaceId)> memberships) : IAccountFullWorthSpaceMembership
    {
        private readonly HashSet<(Guid UserId, Guid FullWorthSpaceId)> allowed = memberships.ToHashSet();

        public Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
            Task.FromResult(allowed.Contains((userId, fullWorthSpaceId)));
    }
}
