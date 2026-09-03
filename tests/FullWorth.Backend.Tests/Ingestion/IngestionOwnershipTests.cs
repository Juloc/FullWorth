using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Ingestion;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Ingestion;

public sealed class IngestionOwnershipTests
{
    [Fact]
    public async Task Bank_ingest_assigns_authorization_owner_and_repairs_an_orphaned_account()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var user = new FullWorthUser
        {
            EmailNormalized = "owner@example.test",
            DisplayName = "Owner"
        };
        var space = new FullWorthSpace { Name = "Test Space", BaseCurrency = "EUR" };
        db.Users.Add(user);
        db.FullWorthSpaces.Add(space);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = space.Id,
            UserId = user.Id,
            Role = FullWorthSpaceRoles.Owner
        });

        var connection = new BankConnection
        {
            FullWorthSpaceId = space.Id,
            Provider = "enable-banking",
            InstitutionName = "Mock ASPSP",
            Country = "DE",
            ProviderSessionId = "session-owner-test",
            Status = "AUTHORIZED",
            AuthorizationUserId = user.Id
        };
        db.BankConnections.Add(connection);
        await db.SaveChangesAsync();

        var batch = new FinanceIngestBatch(
            new BankConnectionBatch(
                connection.Id,
                "enable-banking",
                "Mock ASPSP",
                "DE",
                "session-owner-test",
                "AUTHORIZED",
                DateTimeOffset.UtcNow.AddDays(30),
                DateTimeOffset.UtcNow,
                null,
                space.Id),
            [new AccountBatchItem(
                "owner-test-hash",
                "provider-account-1",
                "Mock ASPSP",
                "Mock Girokonto",
                "Current",
                "checking",
                "EUR",
                "1234",
                true)],
            [],
            []);

        var service = new IngestionService(db);
        await service.IngestAsync(batch, CancellationToken.None);

        var accountId = await db.Accounts.AsNoTracking()
            .Where(account => account.IdentificationHash == "owner-test-hash")
            .Select(account => account.Id)
            .SingleAsync();
        var owner = await db.AccountOwners.AsNoTracking().SingleAsync();
        Assert.Equal(accountId, owner.AccountId);
        Assert.Equal(user.Id, owner.UserId);
        Assert.Equal(AccountOwnershipTypes.Owner, owner.OwnershipType);

        // Simulate an account imported before the ownership fix: the data exists but the ownership
        // row is missing, so owner-gated public account queries cannot return it.
        db.AccountOwners.Remove(await db.AccountOwners.SingleAsync());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await service.IngestAsync(batch, CancellationToken.None);

        var repairedOwner = await db.AccountOwners.AsNoTracking().SingleAsync();
        Assert.Equal(accountId, repairedOwner.AccountId);
        Assert.Equal(user.Id, repairedOwner.UserId);
        Assert.Equal(AccountOwnershipTypes.Owner, repairedOwner.OwnershipType);
    }
}
