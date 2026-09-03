using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Security;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

public sealed class FinanzguruLivePreferenceTests
{
    [Fact]
    public async Task ReimportAfterConnectionWritesToLiveAccountNotArchivedContainer()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();
        var liveId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
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

            const string iban = "DE65500105175456601426";
            var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"finanzguru|{iban}"))).ToLowerInvariant();
            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = archiveId,
                    FullWorthSpaceId = spaceId,
                    Provider = FinanzguruAccountReconciliationService.ImportProvider,
                    IdentificationHash = sourceHash,
                    ProviderAccountId = "finanzguru:archive",
                    InstitutionName = "Finanzguru Import",
                    DisplayName = "Archiv",
                    Currency = "EUR",
                    IbanLast4 = "1426",
                    IsActive = false,
                    IncludeInNetWorth = false
                },
                new FinanceAccount
                {
                    Id = liveId,
                    FullWorthSpaceId = spaceId,
                    Provider = "enable-banking",
                    IdentificationHash = "live",
                    ProviderAccountId = "live",
                    InstitutionName = "Bank",
                    DisplayName = "Girokonto",
                    Currency = "EUR",
                    IbanLast4 = "1426",
                    IsActive = true,
                    IncludeInNetWorth = true
                });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = archiveId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = liveId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            await db.SaveChangesAsync();

            var service = new FinanzguruImportService(
                db,
                new FinanzguruWorkbookReader(),
                new AuditService(db),
                FieldCipher.Null);
            var row = new FinanzguruRow(
                2,
                new DateOnly(2026, 8, 20),
                iban,
                "Girokonto",
                -9.99m,
                "EUR",
                "Shop",
                null,
                "Test",
                null,
                null,
                null,
                false,
                "later-import",
                null,
                null,
                new Dictionary<string, string?>());

            var result = await service.ImportRowsAsync(userId, spaceId, [row], CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(1, result!.AccountsMatched);
            Assert.Equal(1, result.TransactionsImported);
        });

        await factory.SeedAsync(async db =>
        {
            Assert.False(await db.Transactions.AnyAsync(transaction => transaction.AccountId == archiveId));
            var transaction = await db.Transactions.SingleAsync(transaction => transaction.ExternalKey == "finanzguru:later-import");
            Assert.Equal(liveId, transaction.AccountId);
        });
    }
}
