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
            Assert.Contains(liveTransactions, transaction => transaction.ExternalKey == "finanzguru:old-only");

            var providerDuplicate = liveTransactions.Single(transaction => transaction.Id == liveDuplicateId);
            Assert.Equal(categoryId, providerDuplicate.CategoryId);
            Assert.Equal("finanzguru", providerDuplicate.CategorizationSource);
            var allocation = await db.TransactionAllocations.SingleAsync();
            Assert.Equal(liveDuplicateId, allocation.TransactionId);
        });
    }
}
