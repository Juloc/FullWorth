using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Contracts;

public sealed class ContractContinuityDetectionTests
{
    [Fact]
    public async Task Same_contract_continuing_on_new_account_is_detected()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, overlap: false);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ContractContinuityDetectionService>();

        var candidates = await service.DetectForUserAsync(scenario.UserId, scenario.SpaceId, CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(scenario.ContractA, candidate.OlderContractId);
        Assert.Equal(scenario.ContractB, candidate.NewerContractId);
        Assert.Equal(scenario.AccountA, candidate.OlderAccountId);
        Assert.Equal(scenario.AccountB, candidate.NewerAccountId);
        Assert.Equal(new DateOnly(2026, 7, 1), candidate.OlderLastPayment);
        Assert.Equal(new DateOnly(2026, 8, 1), candidate.NewerFirstPayment);
        Assert.True(candidate.Confidence >= .85m);
    }

    [Fact]
    public async Task Overlapping_histories_are_not_treated_as_account_switch()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, overlap: true);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ContractContinuityDetectionService>();

        var candidates = await service.DetectForUserAsync(scenario.UserId, scenario.SpaceId, CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Parallel_account_history_is_not_treated_as_payment_account_change()
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
                DisplayName = "Parallel owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = spaceId,
                Name = "Parallel",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.AddRange(
                Account(oldAccountId, spaceId, "Old"),
                Account(newAccountId, spaceId, "New"));
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = oldAccountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = newAccountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Contracts.Add(Contract(contractId, spaceId, oldAccountId, "NETFLIX"));

            foreach (var monthsBack in new[] { 4, 3, 2, 1 })
                db.Transactions.Add(Transaction(oldAccountId, today.AddMonths(-monthsBack), "NETFLIX"));
            foreach (var monthsBack in new[] { 2, 1, 0 })
                db.Transactions.Add(Transaction(newAccountId, today.AddMonths(-monthsBack), "NETFLIX"));

            await db.SaveChangesAsync();
        });

        var candidate = new ContractCandidate(
            "NETFLIX",
            49.99m,
            "EUR",
            "monthly",
            1,
            today,
            today.AddMonths(1),
            null,
            newAccountId,
            7,
            0m,
            .95m);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ContractContinuityDetectionService>();
        var result = await service.DetectRelationshipsForUserAsync(
            userId,
            spaceId,
            [candidate],
            CancellationToken.None);

        Assert.Empty(result.AccountChanges);
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, bool overlap)
    {
        var scenario = new Scenario(
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
                DisplayName = "Continuity owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.SpaceId,
                Name = "Continuity",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.SpaceId,
                UserId = scenario.UserId,
                Role = FullWorthSpaceRoles.Owner
            });

            db.Accounts.AddRange(
                Account(scenario.AccountA, scenario.SpaceId, "Old account"),
                Account(scenario.AccountB, scenario.SpaceId, "New account"));
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = scenario.AccountA, UserId = scenario.UserId, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = scenario.AccountB, UserId = scenario.UserId, OwnershipType = AccountOwnershipTypes.Owner });

            db.Contracts.AddRange(
                Contract(scenario.ContractA, scenario.SpaceId, scenario.AccountA, "MÜLLER GMBH"),
                Contract(scenario.ContractB, scenario.SpaceId, scenario.AccountB, "MUELLER"));

            var oldDates = overlap
                ? new[] { new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1) }
                : new[] { new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 1) };
            foreach (var date in oldDates)
                db.Transactions.Add(Transaction(scenario.AccountA, date, "MÜLLER GMBH"));

            var newDates = overlap
                ? new[] { new DateOnly(2026, 7, 15), new DateOnly(2026, 8, 15) }
                : new[] { new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1) };
            foreach (var date in newDates)
                db.Transactions.Add(Transaction(scenario.AccountB, date, "MUELLER GMBH"));

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static FinanceAccount Account(Guid id, Guid spaceId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Provider = "manual",
        IdentificationHash = $"continuity-{id:N}",
        ProviderAccountId = $"continuity-{id:N}",
        InstitutionName = "Test",
        DisplayName = name,
        Currency = "EUR"
    };

    private static RecurringContract Contract(Guid id, Guid spaceId, Guid accountId, string provider) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        Name = provider,
        ProviderName = provider,
        AccountId = accountId,
        Amount = 49.99m,
        Currency = "EUR",
        BillingCycle = "monthly",
        Interval = 1,
        IsActive = true
    };

    private static FinanceTransaction Transaction(Guid accountId, DateOnly date, string provider) => new()
    {
        AccountId = accountId,
        ExternalKey = $"continuity-{accountId:N}-{date:yyyyMMdd}",
        Amount = -49.99m,
        Currency = "EUR",
        BookingDate = date,
        Counterparty = provider,
        NormalizedCounterparty = provider,
        CategorizationSource = "none"
    };

    private sealed record Scenario(
        Guid UserId,
        Guid SpaceId,
        Guid AccountA,
        Guid AccountB,
        Guid ContractA,
        Guid ContractB);
}
