using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Signals;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class FinancialDomainSignalDetectionTests
{
    [Fact]
    public async Task Transfer_and_price_change_are_persisted_without_ai_or_finance_mutation()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);

        decimal contractAmountBefore = 0m;
        await factory.SeedAsync(async db =>
        {
            contractAmountBefore = await db.Contracts
                .Where(x => x.Id == scenario.ContractId)
                .Select(x => x.Amount)
                .SingleAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FinancialDomainSignalDetectionService>();
        var count = await service.DetectAndPersistAsync(
            scenario.UserId,
            scenario.SpaceId,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.True(count >= 2);

        var intelligenceDb = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var signals = await intelligenceDb.FinancialSignals.AsNoTracking()
            .Where(x => x.UserId == scenario.UserId && x.FullWorthSpaceId == scenario.SpaceId)
            .ToListAsync();

        Assert.Contains(signals, x => x.Source == "detector:transfer-candidate");
        var price = Assert.Single(signals.Where(x => x.Source == "detector:contract-price-change"));
        Assert.Equal(scenario.ContractId.ToString("N"), price.SubjectId);
        Assert.Equal(2m, price.ImpactAmount);
        Assert.Equal("EUR", price.ImpactCurrency);
        Assert.Empty(await intelligenceDb.AiRuns.AsNoTracking().ToListAsync());

        await factory.SeedAsync(async db =>
        {
            var contractAmountAfter = await db.Contracts
                .Where(x => x.Id == scenario.ContractId)
                .Select(x => x.Amount)
                .SingleAsync();
            Assert.Equal(contractAmountBefore, contractAmountAfter);
            Assert.Empty(await db.PriceChangeSuggestions.AsNoTracking().ToListAsync());
            Assert.All(
                await db.Transactions
                    .Where(x => x.Id == scenario.TransferOutId || x.Id == scenario.TransferInId)
                    .ToListAsync(),
                tx =>
                {
                    Assert.False(tx.IsTransfer);
                    Assert.Null(tx.TransferGroupId);
                });
        });
    }

    [Fact]
    public async Task Accepted_contract_on_old_account_emits_account_change_when_recurrence_moves()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var oldAccountId = Guid.NewGuid();
        var newAccountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Account change owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = spaceId,
                Name = "Account change",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.AddRange(
                Account(oldAccountId, spaceId, "Old payment"),
                Account(newAccountId, spaceId, "New payment"));
            db.AccountOwners.AddRange(Owner(oldAccountId, userId), Owner(newAccountId, userId));
            db.Contracts.Add(new RecurringContract
            {
                Id = contractId,
                FullWorthSpaceId = spaceId,
                Name = "NETFLIX",
                ProviderName = "NETFLIX",
                AccountId = oldAccountId,
                Amount = 12.99m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                AutoDetected = true,
                IsActive = true
            });

            for (var monthsBack = 4; monthsBack >= 1; monthsBack--)
            {
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = oldAccountId,
                    ExternalKey = $"netflix-old-{monthsBack}",
                    Amount = -12.99m,
                    Currency = "EUR",
                    BookingDate = today.AddMonths(-monthsBack),
                    Counterparty = "NETFLIX",
                    NormalizedCounterparty = "netflix",
                    CategorizationSource = "none",
                    RawJson = "{}"
                });
            }
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = newAccountId,
                ExternalKey = "netflix-new-account",
                Amount = -12.99m,
                Currency = "EUR",
                BookingDate = today,
                Counterparty = "NETFLIX",
                NormalizedCounterparty = "netflix",
                CategorizationSource = "none",
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FinancialDomainSignalDetectionService>();
        await service.DetectAndPersistAsync(userId, spaceId, DateTimeOffset.UtcNow, CancellationToken.None);

        var intelligenceDb = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var signal = Assert.Single(await intelligenceDb.FinancialSignals.AsNoTracking()
            .Where(x => x.UserId == userId &&
                        x.FullWorthSpaceId == spaceId &&
                        x.Source == "detector:contract-account-change")
            .ToListAsync());

        Assert.Equal(contractId.ToString("N"), signal.SubjectId);
        Assert.Equal("insights.contract.paymentAccountChanged", signal.TitleKey);
        Assert.Equal(FinancialSignalSeverities.Attention, signal.Severity);

        await factory.SeedAsync(async db =>
        {
            var contract = await db.Contracts.AsNoTracking().SingleAsync(x => x.Id == contractId);
            Assert.Equal(oldAccountId, contract.AccountId);
            Assert.Empty(await db.Contracts.AsNoTracking()
                .Where(x => x.MergedIntoContractId == contractId)
                .ToListAsync());
        });
    }

    [Fact]
    public async Task Linking_transfer_resolves_its_shadow_signal_on_next_detection()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FinancialDomainSignalDetectionService>();
        var now = DateTimeOffset.UtcNow;
        await service.DetectAndPersistAsync(scenario.UserId, scenario.SpaceId, now, CancellationToken.None);

        var intelligenceDb = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var before = Assert.Single(await intelligenceDb.FinancialSignals
            .Where(x => x.Source == "detector:transfer-candidate")
            .ToListAsync());
        Assert.Null(before.ResolvedAt);

        await factory.SeedAsync(async db =>
        {
            var groupId = Guid.NewGuid();
            var rows = await db.Transactions
                .Where(x => x.Id == scenario.TransferOutId || x.Id == scenario.TransferInId)
                .ToListAsync();
            foreach (var row in rows)
            {
                row.IsTransfer = true;
                row.TransferGroupId = groupId;
            }
            await db.SaveChangesAsync();
        });

        await service.DetectAndPersistAsync(
            scenario.UserId,
            scenario.SpaceId,
            now.AddMinutes(1),
            CancellationToken.None);

        var after = await intelligenceDb.FinancialSignals.AsNoTracking()
            .SingleAsync(x => x.Id == before.Id);
        Assert.NotNull(after.ResolvedAt);
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.UserId,
                EmailNormalized = $"{scenario.UserId:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Domain signal owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.SpaceId,
                Name = "Signals",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.SpaceId,
                UserId = scenario.UserId,
                Role = FullWorthSpaceRoles.Owner
            });

            db.Accounts.AddRange(
                Account(scenario.TransferAccountA, scenario.SpaceId, "Transfer A"),
                Account(scenario.TransferAccountB, scenario.SpaceId, "Transfer B"),
                Account(scenario.ContractAccount, scenario.SpaceId, "Contract"));
            db.AccountOwners.AddRange(
                Owner(scenario.TransferAccountA, scenario.UserId),
                Owner(scenario.TransferAccountB, scenario.UserId),
                Owner(scenario.ContractAccount, scenario.UserId));

            db.Contracts.Add(new RecurringContract
            {
                Id = scenario.ContractId,
                FullWorthSpaceId = scenario.SpaceId,
                Name = "ACME STREAM",
                ProviderName = "ACME STREAM",
                AccountId = scenario.ContractAccount,
                Amount = 10m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                IsActive = true,
                AutoDetected = false
            });

            db.Transactions.AddRange(
                new FinanceTransaction
                {
                    Id = scenario.TransferOutId,
                    AccountId = scenario.TransferAccountA,
                    ExternalKey = "transfer-out",
                    Amount = -200m,
                    Currency = "EUR",
                    BookingDate = new DateOnly(2026, 9, 2),
                    Counterparty = "Own transfer",
                    NormalizedCounterparty = "OWN TRANSFER",
                    CategorizationSource = "none",
                    RawJson = "{}"
                },
                new FinanceTransaction
                {
                    Id = scenario.TransferInId,
                    AccountId = scenario.TransferAccountB,
                    ExternalKey = "transfer-in",
                    Amount = 200m,
                    Currency = "EUR",
                    BookingDate = new DateOnly(2026, 9, 2),
                    Counterparty = "Own transfer",
                    NormalizedCounterparty = "OWN TRANSFER",
                    CategorizationSource = "none",
                    RawJson = "{}"
                },
                PriceTransaction(scenario.ContractAccount, "price-old", new DateOnly(2026, 7, 5), 10m),
                PriceTransaction(scenario.ContractAccount, "price-new", new DateOnly(2026, 8, 5), 12m),
                new FinanceTransaction
                {
                    AccountId = scenario.ContractAccount,
                    ExternalKey = "unrelated-newer-debit",
                    Amount = -99m,
                    Currency = "EUR",
                    BookingDate = new DateOnly(2026, 9, 6),
                    Counterparty = "OTHER SHOP",
                    NormalizedCounterparty = "OTHER SHOP",
                    CategorizationSource = "none",
                    RawJson = "{}"
                });

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static FinanceAccount Account(Guid id, Guid spaceId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Provider = "manual",
        IdentificationHash = $"signals-{id:N}",
        ProviderAccountId = $"signals-{id:N}",
        InstitutionName = "Test",
        DisplayName = name,
        Currency = "EUR"
    };

    private static AccountOwner Owner(Guid accountId, Guid userId) => new()
    {
        AccountId = accountId,
        UserId = userId,
        OwnershipType = AccountOwnershipTypes.Owner
    };

    private static FinanceTransaction PriceTransaction(Guid accountId, string key, DateOnly date, decimal amount) => new()
    {
        AccountId = accountId,
        ExternalKey = key,
        Amount = -amount,
        Currency = "EUR",
        BookingDate = date,
        Counterparty = "ACME STREAM",
        NormalizedCounterparty = "ACME STREAM",
        Description = "Subscription",
        CategorizationSource = "none",
        RawJson = "{}"
    };

    private sealed record Scenario(
        Guid UserId,
        Guid SpaceId,
        Guid TransferAccountA,
        Guid TransferAccountB,
        Guid ContractAccount,
        Guid ContractId,
        Guid TransferOutId,
        Guid TransferInId);
}
