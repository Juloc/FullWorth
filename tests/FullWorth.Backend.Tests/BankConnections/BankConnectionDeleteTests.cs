using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.BankConnections;

// Disconnecting a bank permanently deletes the connection and everything synced under it (accounts,
// balances, transactions), detaches recurring contracts from the account, and leaves unrelated
// accounts untouched. Members only; non-members get a 404-equivalent false.
public sealed class BankConnectionDeleteTests
{
    [Fact]
    public async Task Disconnect_DeletesConnectionAccountsAndData_DetachesContracts_LeavesOthersUntouched()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var spaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var bankAccount = Guid.NewGuid();
        var manualAccount = Guid.NewGuid();
        var contractId = Guid.NewGuid();

        await using (var db = database.CreateContext())
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EX.COM", DisplayName = "Owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Home", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            db.BankConnections.Add(new BankConnection { Id = connectionId, FullWorthSpaceId = spaceId, Provider = "enable-banking", InstitutionName = "Bank", Country = "DE", ProviderSessionId = "s" });

            db.Accounts.Add(new FinanceAccount { Id = bankAccount, FullWorthSpaceId = spaceId, BankConnectionId = connectionId, Provider = "enable-banking", IdentificationHash = "h1", ProviderAccountId = "p1", InstitutionName = "Bank", DisplayName = "Giro", Currency = "EUR", IsActive = true });
            db.Accounts.Add(new FinanceAccount { Id = manualAccount, FullWorthSpaceId = spaceId, BankConnectionId = null, Provider = "manual", IdentificationHash = "h2", ProviderAccountId = "p2", InstitutionName = "Cash", DisplayName = "Cash", Currency = "EUR", IsActive = true });
            db.AccountOwners.Add(new AccountOwner { AccountId = bankAccount, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.AccountOwners.Add(new AccountOwner { AccountId = manualAccount, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });

            db.Transactions.Add(new FinanceTransaction { AccountId = bankAccount, ExternalKey = "tx-bank", Amount = -10m, Currency = "EUR" });
            db.Transactions.Add(new FinanceTransaction { AccountId = manualAccount, ExternalKey = "tx-manual", Amount = -5m, Currency = "EUR" });
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = bankAccount, Amount = 100m, Currency = "EUR", BalanceType = "closingAvailable", CapturedAt = DateTimeOffset.UtcNow });

            // A recurring contract tied to the bank account must survive with its AccountId cleared.
            db.Contracts.Add(new RecurringContract { Id = contractId, FullWorthSpaceId = spaceId, Name = "Netflix", Amount = 12m, Currency = "EUR", AccountId = bankAccount, IsActive = true });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            var store = new BankConnectionStore(db);
            Assert.True(await store.DeleteForUserAsync(userId, spaceId, connectionId, CancellationToken.None));
        }

        await using (var db = database.CreateContext())
        {
            Assert.Empty(await db.BankConnections.ToListAsync());
            var accounts = await db.Accounts.Select(a => a.Id).ToListAsync();
            Assert.Equal(new[] { manualAccount }, accounts);                                  // bank account gone, manual kept
            var txs = await db.Transactions.Select(t => t.ExternalKey).ToListAsync();
            Assert.Equal(new[] { "tx-manual" }, txs);                                          // bank tx gone, manual kept
            Assert.Empty(await db.BalanceSnapshots.ToListAsync());
            Assert.Empty(await db.AccountOwners.Where(o => o.AccountId == bankAccount).ToListAsync()); // cascaded
            var contract = await db.Contracts.SingleAsync(c => c.Id == contractId);
            Assert.Null(contract.AccountId);                                                  // detached, not deleted
        }
    }

    [Fact]
    public async Task Disconnect_RejectsNonMember_AndKeepsData()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var spaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        await using (var db = database.CreateContext())
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Home", BaseCurrency = "EUR" });
            db.BankConnections.Add(new BankConnection { Id = connectionId, FullWorthSpaceId = spaceId, Provider = "enable-banking", InstitutionName = "Bank", Country = "DE", ProviderSessionId = "s" });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            var store = new BankConnectionStore(db);
            Assert.False(await store.DeleteForUserAsync(Guid.NewGuid(), spaceId, connectionId, CancellationToken.None));
            Assert.Single(await db.BankConnections.ToListAsync());
        }
    }
}
