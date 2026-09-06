using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

public sealed class FinanzguruReconciliationTests
{
    [Fact]
    public async Task ArchivedHistoryMovesToLaterConnectedAccountAndOverlapsAreMerged()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var importAccountId = Guid.NewGuid();
        var liveAccountId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var importedDuplicateId = Guid.NewGuid();
        var importedHistoricalId = Guid.NewGuid();
        var liveDuplicateId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Import owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Categories.Add(new FinanceCategory
            {
                Id = categoryId,
                FullWorthSpaceId = spaceId,
                Key = "finanzguru-shopping",
                Name = "Shopping"
            });
            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = importAccountId,
                    FullWorthSpaceId = spaceId,
                    Provider = FinanzguruAccountReconciliationService.ImportProvider,
                    IdentificationHash = "fg-import",
                    ProviderAccountId = "finanzguru:history",
                    InstitutionName = "Finanzguru Import",
                    DisplayName = "Altes Girokonto",
                    Currency = "EUR",
                    IbanLast4 = "1426",
                    IsActive = true,
                    IncludeInNetWorth = false
                },
                new FinanceAccount
                {
                    Id = liveAccountId,
                    FullWorthSpaceId = spaceId,
                    Provider = "enable-banking",
                    IdentificationHash = "live-account",
                    ProviderAccountId = "provider-account",
                    InstitutionName = "Bank",
                    DisplayName = "Girokonto",
                    Currency = "EUR",
                    IbanLast4 = "1426",
                    IsActive = true,
                    IncludeInNetWorth = true
                });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = importAccountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = liveAccountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });

            db.Transactions.AddRange(
                new FinanceTransaction
                {
                    Id = importedDuplicateId,
                    AccountId = importAccountId,
                    CategoryId = categoryId,
                    ExternalKey = "finanzguru:duplicate",
                    UseForBalanceHistory = false,
                    ProviderTransactionId = "duplicate",
                    BookingDate = new DateOnly(2026, 8, 20),
                    ValueDate = new DateOnly(2026, 8, 20),
                    Amount = -30m,
                    Currency = "EUR",
                    Counterparty = "Amazon",
                    NormalizedCounterparty = MerchantNormalization.Normalize("Amazon"),
                    Description = "Bestellung",
                    Status = "BOOK",
                    CategorizationSource = "finanzguru",
                    RawJson = "{}"
                },
                new FinanceTransaction
                {
                    Id = importedHistoricalId,
                    AccountId = importAccountId,
                    ExternalKey = "finanzguru:old-only",
                    UseForBalanceHistory = false,
                    ProviderTransactionId = "old-only",
                    BookingDate = new DateOnly(2024, 1, 5),
                    ValueDate = new DateOnly(2024, 1, 5),
                    Amount = -12m,
                    Currency = "EUR",
                    Counterparty = "Baecker",
                    NormalizedCounterparty = MerchantNormalization.Normalize("Baecker"),
                    Status = "BOOK",
                    RawJson = "{}"
                },
                new FinanceTransaction
                {
                    Id = liveDuplicateId,
                    AccountId = liveAccountId,
                    ExternalKey = "enable-banking:provider-duplicate",
                    ProviderTransactionId = "provider-duplicate",
                    BookingDate = new DateOnly(2026, 8, 20),
                    ValueDate = new DateOnly(2026, 8, 20),
                    Amount = -30m,
                    Currency = "EUR",
                    Counterparty = "Amazon",
                    NormalizedCounterparty = MerchantNormalization.Normalize("Amazon"),
                    Description = "Provider text",
                    Status = "BOOK",
                    CategorizationSource = "none",
                    RawJson = "{}"
                });
            db.TransactionAllocations.Add(new TransactionAllocation
            {
                TransactionId = importedDuplicateId,
                CategoryId = categoryId,
                Amount = -30m
            });
            await db.SaveChangesAsync();

            var live = await db.Accounts.SingleAsync(account => account.Id == liveAccountId);
            var service = new FinanzguruAccountReconciliationService(db, new AuditService(db));
            var result = await service.ReconcileAsync(spaceId, [live], CancellationToken.None);

            Assert.Equal(1, result.AccountsReconciled);
            Assert.Equal(1, result.TransactionsMoved);
            Assert.Equal(1, result.TransactionsMerged);
        });

        await factory.SeedAsync(async db =>
        {
            var archive = await db.Accounts.SingleAsync(account => account.Id == importAccountId);
            Assert.False(archive.IsActive);
            Assert.False(archive.IncludeInNetWorth);
            Assert.False(await db.Transactions.AnyAsync(transaction => transaction.AccountId == importAccountId));

            var liveTransactions = await db.Transactions
                .Where(transaction => transaction.AccountId == liveAccountId)
                .OrderBy(transaction => transaction.BookingDate)
                .ToListAsync();
            Assert.Equal(2, liveTransactions.Count);
            var movedHistory = Assert.Single(liveTransactions, transaction => transaction.ExternalKey == "finanzguru:old-only");
            Assert.False(movedHistory.UseForBalanceHistory);

            var providerDuplicate = liveTransactions.Single(transaction => transaction.Id == liveDuplicateId);
            Assert.Equal(categoryId, providerDuplicate.CategoryId);
            Assert.Equal("finanzguru", providerDuplicate.CategorizationSource);
            var allocation = await db.TransactionAllocations.SingleAsync();
            Assert.Equal(liveDuplicateId, allocation.TransactionId);
        });
    }

    [Fact]
    public async Task ExplicitLinkPersistsMappingTrustsImportedHistoryAndAcceptsCurrentBalance()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var importAccountId = Guid.NewGuid();
        var targetAccountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        db.Users.Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
            DisplayName = "Owner",
            IsActive = true
        });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Space", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = spaceId,
            UserId = userId,
            Role = FullWorthSpaceRoles.Owner
        });
        db.Accounts.AddRange(
            new FinanceAccount
            {
                Id = importAccountId,
                FullWorthSpaceId = spaceId,
                Provider = FinanzguruAccountReconciliationService.ImportProvider,
                IdentificationHash = "fg-no-iban",
                ProviderAccountId = "finanzguru:fg-no-iban",
                InstitutionName = "Finanzguru Import",
                DisplayName = "Historie",
                Currency = "EUR",
                IsActive = false,
                IncludeInNetWorth = false
            },
            new FinanceAccount
            {
                Id = targetAccountId,
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = "target",
                ProviderAccountId = "target",
                InstitutionName = "Bank",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
        db.AccountOwners.AddRange(
            new AccountOwner { AccountId = importAccountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner },
            new AccountOwner { AccountId = targetAccountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
        db.Transactions.Add(new FinanceTransaction
        {
            Id = transactionId,
            AccountId = importAccountId,
            ExternalKey = "finanzguru:2022-1",
            Status = "BOOK",
            BookingDate = new DateOnly(2022, 4, 1),
            ValueDate = new DateOnly(2022, 4, 1),
            Amount = -50m,
            Currency = "EUR",
            UseForBalanceHistory = false,
            RawJson = "{}"
        });
        await db.SaveChangesAsync();

        var service = new FinanzguruAccountReconciliationService(db, new AuditService(db));
        var result = await service.LinkExplicitAsync(
            userId,
            spaceId,
            importAccountId,
            targetAccountId,
            1_000m,
            "EUR",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.TransactionsMoved);
        Assert.Equal(1, result.TransactionsTrustedForHistory);
        Assert.True(result.CurrentBalanceAdded);

        var archive = await db.Accounts.AsNoTracking().SingleAsync(account => account.Id == importAccountId);
        Assert.Equal(targetAccountId, archive.ImportLinkedAccountId);

        var historical = await db.Transactions.AsNoTracking().SingleAsync(transaction => transaction.Id == transactionId);
        Assert.Equal(targetAccountId, historical.AccountId);
        Assert.True(historical.UseForBalanceHistory);

        var balance = await db.BalanceSnapshots.AsNoTracking().SingleAsync(snapshot => snapshot.AccountId == targetAccountId);
        Assert.Equal(1_000m, balance.Amount);
        Assert.Equal("manualCurrent", balance.BalanceType);
    }

    [Fact]
    public async Task ConfirmAttachedHistoryTrustsPreMatchedImportedRows()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var targetAccountId = Guid.NewGuid();

        db.Users.Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
            DisplayName = "Owner",
            IsActive = true
        });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Space", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = spaceId,
            UserId = userId,
            Role = FullWorthSpaceRoles.Owner
        });
        db.Accounts.Add(new FinanceAccount
        {
            Id = targetAccountId,
            FullWorthSpaceId = spaceId,
            Provider = "enable-banking",
            IdentificationHash = "live",
            ProviderAccountId = "live",
            InstitutionName = "Bank",
            DisplayName = "Girokonto",
            Currency = "EUR",
            IsActive = true,
            IncludeInNetWorth = true
        });
        db.AccountOwners.Add(new AccountOwner
        {
            AccountId = targetAccountId,
            UserId = userId,
            OwnershipType = AccountOwnershipTypes.Owner
        });
        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = targetAccountId,
            Amount = 500m,
            Currency = "EUR",
            BalanceType = "closingBooked",
            ReferenceDate = new DateOnly(2026, 9, 6),
            CapturedAt = DateTimeOffset.UtcNow
        });
        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = targetAccountId,
            ExternalKey = "finanzguru:old",
            Status = "BOOK",
            BookingDate = new DateOnly(2021, 1, 2),
            ValueDate = new DateOnly(2021, 1, 2),
            Amount = -10m,
            Currency = "EUR",
            UseForBalanceHistory = false,
            RawJson = "{}"
        });
        await db.SaveChangesAsync();

        var service = new FinanzguruAccountReconciliationService(db, new AuditService(db));
        var result = await service.ConfirmAttachedHistoryAsync(
            userId,
            spaceId,
            targetAccountId,
            null,
            null,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.TransactionsTrustedForHistory);
        Assert.False(result.CurrentBalanceAdded);
        Assert.True(await db.Transactions.AsNoTracking().AllAsync(transaction => transaction.UseForBalanceHistory));
    }

}
