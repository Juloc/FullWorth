using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Ein Konto ohne erklaerte Waehrung laesst sich verknuepfen (#112).
///
/// Das PayPal-Konto erschien in der Zielauswahl als "PayPal · XXX", ausgegraut, und liess sich mit
/// keinem Import verbinden. <c>XXX</c> ist der ISO-4217-Code fuer "keine Waehrung" - ein PayPal-Wallet
/// meldet ihn, weil es mehrere Waehrungen zugleich haelt. Er sagt "hier steht keine", nicht "hier
/// steht eine andere".
///
/// Geprueft wurde an vier Stellen auf GLEICHHEIT. Damit wurde aus der fehlenden Angabe ein
/// Widerspruch. Die Sicherung, um die es wirklich geht, bleibt: Euro-Buchungen landen nicht auf einem
/// Dollar-Konto.
/// </summary>
public sealed class PayPalCurrencyLinkTests
{
    [Theory]
    [InlineData("XXX")]
    [InlineData("")]
    public async Task AnAccountWithoutADeclaredCurrencyCanBeLinkedAndAdoptsTheImportCurrency(string targetCurrency)
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory, targetCurrency);

        await factory.SeedAsync(async db =>
        {
            var service = new FinanzguruAccountReconciliationService(db, new AuditService(db));
            var result = await service.LinkExplicitAsync(
                seed.UserId, seed.SpaceId, seed.ImportAccountId, seed.TargetAccountId, 250m, "EUR", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(1, result!.TransactionsMoved);

            // Das Konto hat jetzt eine Waehrung - aus dem Import, der eine hatte. Sonst bliebe "XXX"
            // stehen und der naechste Vergleich scheiterte an derselben Stelle.
            var target = await db.Accounts.AsNoTracking().SingleAsync(account => account.Id == seed.TargetAccountId);
            Assert.Equal("EUR", target.Currency);

            var moved = await db.Transactions.AsNoTracking().SingleAsync(transaction => transaction.Id == seed.TransactionId);
            Assert.Equal(seed.TargetAccountId, moved.AccountId);
        });
    }

    /// <summary>
    /// Die Sicherung bleibt: zwei Konten, die BEIDE eine Waehrung nennen und verschiedene, gehoeren
    /// nicht zusammen. Waere das aufgeweicht, landeten Euro-Buchungen auf einem Dollar-Konto.
    /// </summary>
    [Fact]
    public async Task TwoDeclaredAndDifferentCurrenciesStillRefuseToLink()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory, "USD");

        await factory.SeedAsync(async db =>
        {
            var service = new FinanzguruAccountReconciliationService(db, new AuditService(db));
            await Assert.ThrowsAsync<ArgumentException>(() => service.LinkExplicitAsync(
                seed.UserId, seed.SpaceId, seed.ImportAccountId, seed.TargetAccountId, 250m, "USD", CancellationToken.None));
        });
    }

    private sealed record Seed(Guid UserId, Guid SpaceId, Guid ImportAccountId, Guid TargetAccountId, Guid TransactionId);

    private static async Task<Seed> SeedAsync(BackendWebApplicationFactory factory, string targetCurrency)
    {
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = seed.UserId,
                EmailNormalized = $"{seed.UserId:N}@LOCAL.TEST",
                DisplayName = "Test",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = seed.SpaceId, Name = "Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = seed.SpaceId,
                UserId = seed.UserId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = seed.ImportAccountId,
                    FullWorthSpaceId = seed.SpaceId,
                    Provider = FinanzguruAccountReconciliationService.ImportProvider,
                    IdentificationHash = "fg-paypal",
                    ProviderAccountId = "finanzguru:fg-paypal",
                    InstitutionName = "Finanzguru Import",
                    DisplayName = "PayPal Historie",
                    Currency = "EUR",
                    IsActive = false,
                    IncludeInNetWorth = false
                },
                new FinanceAccount
                {
                    Id = seed.TargetAccountId,
                    FullWorthSpaceId = seed.SpaceId,
                    Provider = "enablebanking",
                    IdentificationHash = "paypal-wallet",
                    ProviderAccountId = "paypal-wallet",
                    InstitutionName = "PayPal",
                    DisplayName = "PayPal",
                    // Genau so meldet es ein PayPal-Wallet: mehrere Waehrungen, also keine.
                    Currency = targetCurrency,
                    IsActive = true,
                    IncludeInNetWorth = true
                });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = seed.ImportAccountId, UserId = seed.UserId, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = seed.TargetAccountId, UserId = seed.UserId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = seed.TransactionId,
                AccountId = seed.ImportAccountId,
                ExternalKey = "finanzguru:paypal-1",
                Status = "BOOK",
                BookingDate = new DateOnly(2026, 3, 1),
                ValueDate = new DateOnly(2026, 3, 1),
                Amount = -12.34m,
                Currency = "EUR",
                UseForBalanceHistory = false,
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        });
        return seed;
    }
}
