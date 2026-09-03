using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Transactions;

public sealed class TransactionClassificationTests
{
    [Fact]
    public async Task ManualClassificationPersistsCategoryIgnoreTransferAndSource()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var categoryId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using (var db = database.CreateContext())
        {
            var connection = new BankConnection
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "test",
                InstitutionName = "Test Bank",
                Country = "DE",
                ProviderSessionId = "classification-session"
            };
            var account = new FinanceAccount
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                BankConnectionId = connection.Id,
                Provider = "test",
                IdentificationHash = "classification-account",
                ProviderAccountId = "classification-account",
                InstitutionName = "Test Bank",
                DisplayName = "Classification",
                Currency = "EUR"
            };
            var user = new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Classification owner",
                IsActive = true
            };
            db.Users.Add(user);
            db.BankConnections.Add(connection);
            db.Accounts.Add(account);
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account.Id,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Categories.Add(new FinanceCategory
            {
                Id = categoryId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = "manual-test",
                Name = "Manual test"
            });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transactionId,
                AccountId = account.Id,
                ExternalKey = "classification-tx",
                Amount = -42.50m,
                Currency = "EUR",
                CategorizationSource = "none"
            });
            await db.SaveChangesAsync();

            var store = new TransactionStore(db);
            var result = await store.ClassifyForOwnerAsync(
                userId,
                FullWorthSpaceDefaults.LegacyId,
                transactionId,
                new TransactionClassification(categoryId, true, true),
                CancellationToken.None);
            Assert.Equal(TransactionClassificationResult.Updated, result);
        }

        await using var verification = database.CreateContext();
        var stored = await verification.Transactions.AsNoTracking().SingleAsync(x => x.Id == transactionId);
        Assert.Equal(categoryId, stored.CategoryId);
        Assert.True(stored.IsIgnored);
        Assert.True(stored.IsTransfer);
        Assert.Equal("manual", stored.CategorizationSource);
    }

    [Fact]
    public async Task ClassificationWritesAndClearsTheUserNote()
    {
        // §9.3: the free-text note is editable on any transaction (here an imported one). The note is
        // trimmed on write, and a blank note clears it back to null.
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var userId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        await using (var db = database.CreateContext())
        {
            var connection = new BankConnection { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = "note-session" };
            var account = new FinanceAccount { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, BankConnectionId = connection.Id, Provider = "test", IdentificationHash = "note-acct", ProviderAccountId = "note-acct", InstitutionName = "Bank", DisplayName = "Note", Currency = "EUR" };
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EX.COM", DisplayName = "Owner", IsActive = true });
            db.BankConnections.Add(connection);
            db.Accounts.Add(account);
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.AccountOwners.Add(new AccountOwner { AccountId = account.Id, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction { Id = transactionId, AccountId = account.Id, ExternalKey = "note-tx", Amount = -10m, Currency = "EUR" });
            await db.SaveChangesAsync();

            var store = new TransactionStore(db);
            Assert.Equal(TransactionClassificationResult.Updated, await store.ClassifyForOwnerAsync(userId, FullWorthSpaceDefaults.LegacyId, transactionId,
                new TransactionClassification(null, false, false, UserNote: "  Reimbursed by Alex  "), CancellationToken.None));
        }
        await using (var db = database.CreateContext())
            Assert.Equal("Reimbursed by Alex", await db.Transactions.Where(x => x.Id == transactionId).Select(x => x.UserNote).SingleAsync());

        await using (var db = database.CreateContext())
        {
            var store = new TransactionStore(db);
            Assert.Equal(TransactionClassificationResult.Updated, await store.ClassifyForOwnerAsync(userId, FullWorthSpaceDefaults.LegacyId, transactionId,
                new TransactionClassification(null, false, false, UserNote: "   "), CancellationToken.None));
        }
        await using (var db = database.CreateContext())
            Assert.Null(await db.Transactions.Where(x => x.Id == transactionId).Select(x => x.UserNote).SingleAsync());
    }

    [Fact]
    public async Task ManualClassificationRejectsCategoryFromAnotherFullWorthSpace()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var userId = Guid.NewGuid();
        var otherSpace = new FullWorthSpace { Name = "Other", BaseCurrency = "EUR" };
        var connection = new BankConnection
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            Provider = "test",
            InstitutionName = "Test Bank",
            Country = "DE",
            ProviderSessionId = "classification-cross-space"
        };
        var account = new FinanceAccount
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            BankConnectionId = connection.Id,
            Provider = "test",
            IdentificationHash = "classification-cross-space-account",
            ProviderAccountId = "classification-cross-space-account",
            InstitutionName = "Test Bank",
            DisplayName = "Classification",
            Currency = "EUR"
        };
        var category = new FinanceCategory
        {
            FullWorthSpaceId = otherSpace.Id,
            Key = "other-space-category",
            Name = "Other space"
        };
        var transaction = new FinanceTransaction
        {
            AccountId = account.Id,
            ExternalKey = "classification-cross-space-tx",
            Amount = -10m,
            Currency = "EUR"
        };
        var user = new FullWorthUser
        {
            Id = userId,
            EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
            DisplayName = "Classification owner",
            IsActive = true
        };

        db.AddRange(user, otherSpace, connection, account, category, transaction);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            UserId = userId,
            Role = FullWorthSpaceRoles.Member
        });
        db.AccountOwners.Add(new AccountOwner
        {
            AccountId = account.Id,
            UserId = userId,
            OwnershipType = AccountOwnershipTypes.Owner
        });
        await db.SaveChangesAsync();

        var store = new TransactionStore(db);
        var result = await store.ClassifyForOwnerAsync(
            userId,
            FullWorthSpaceDefaults.LegacyId,
            transaction.Id,
            new TransactionClassification(category.Id, false, false),
            CancellationToken.None);

        Assert.Equal(TransactionClassificationResult.InvalidCategory, result);
        db.ChangeTracker.Clear();
        Assert.Null(await db.Transactions.Where(x => x.Id == transaction.Id).Select(x => x.CategoryId).SingleAsync());
    }

    [Fact]
    public async Task TransferPurposeIsStoredForATransferAndClearedWhenTransferIsRemoved()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var userId = Guid.NewGuid();
        var txId = Guid.NewGuid();

        await using (var db = database.CreateContext())
        {
            var connection = new BankConnection { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = "purpose-session" };
            var account = new FinanceAccount { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, BankConnectionId = connection.Id, Provider = "test", IdentificationHash = "purpose-account", ProviderAccountId = "purpose-account", InstitutionName = "Bank", DisplayName = "Purpose", Currency = "EUR" };
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EX.COM".ToUpperInvariant(), DisplayName = "Owner", IsActive = true });
            db.BankConnections.Add(connection);
            db.Accounts.Add(account);
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.AccountOwners.Add(new AccountOwner { AccountId = account.Id, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction { Id = txId, AccountId = account.Id, ExternalKey = "purpose-tx", Amount = -200m, Currency = "EUR" });
            await db.SaveChangesAsync();

            var store = new TransactionStore(db);
            // Mark as transfer with a savings purpose.
            await store.ClassifyForOwnerAsync(userId, FullWorthSpaceDefaults.LegacyId, txId,
                new TransactionClassification(null, false, IsTransfer: true, TransferPurpose: "savings"), CancellationToken.None);
        }
        await using (var db = database.CreateContext())
        {
            var stored = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == txId);
            Assert.True(stored.IsTransfer);
            Assert.Equal("savings", stored.TransferPurpose);

            // Removing the transfer flag must clear the purpose so no stale label remains.
            var store = new TransactionStore(db);
            await store.ClassifyForOwnerAsync(userId, FullWorthSpaceDefaults.LegacyId, txId,
                new TransactionClassification(null, false, IsTransfer: false, TransferPurpose: "savings"), CancellationToken.None);
        }
        await using (var db = database.CreateContext())
        {
            var stored = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == txId);
            Assert.False(stored.IsTransfer);
            Assert.Null(stored.TransferPurpose);
        }
    }
}
