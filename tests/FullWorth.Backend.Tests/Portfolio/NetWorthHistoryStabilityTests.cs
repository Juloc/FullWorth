using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// A snapshot recorded on its own day holds what the accounts actually were that day. The nightly rebuild
/// is an estimate — it back-casts today's balance through today's bookings — and it used to overwrite that
/// measurement on every run, six hours apart. So the past moved whenever a booking was re-categorised or an
/// account was removed, and the real number was gone for good.
/// </summary>
public sealed class NetWorthHistoryStabilityTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // Current balance 1 000, a +200 booking yesterday, and a day three days back that was measured at
    // 500. The back-cast would derive 800 for that day (1 000 - 200) and overwrite the 500 that was
    // actually recorded there.
    [Fact]
    public async Task A_day_that_was_measured_is_not_rewritten_by_a_later_rebuild()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        var measuredDay = Today.AddDays(-3);
        await AddMeasuredSnapshotAsync(factory, scenario, measuredDay, accounts: 500m);
        await AddBookingAsync(factory, scenario, Today.AddDays(-1), 200m);

        await RebuildAsync(factory, scenario);

        Assert.Equal(500m, await AccountsOnAsync(factory, scenario, measuredDay));
        // Today is the live measurement and must follow the current balance.
        Assert.Equal(1000m, await AccountsOnAsync(factory, scenario, Today));
    }

    [Fact]
    public async Task A_day_with_no_snapshot_is_still_filled_in()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        // A booking is the evidence that reaches back: without any, the history deliberately starts
        // today rather than inventing days nothing is known about.
        await AddBookingAsync(factory, scenario, Today.AddDays(-2), 200m);

        await RebuildAsync(factory, scenario);

        Assert.Equal(1000m, await AccountsOnAsync(factory, scenario, Today.AddDays(-1)));
        Assert.Equal(1000m, await AccountsOnAsync(factory, scenario, Today.AddDays(-2)));
    }

    // The days BEFORE a measured day must be derived from what was recorded there, not from today's
    // balance carried straight across it.
    [Fact]
    public async Task The_back_cast_re_anchors_on_a_measured_day()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        var measuredDay = Today.AddDays(-3);
        await AddMeasuredSnapshotAsync(factory, scenario, measuredDay, accounts: 500m);
        await AddBookingAsync(factory, scenario, Today.AddDays(-1), 200m);
        // Only here to reach further back than the measured day.
        await AddBookingAsync(factory, scenario, Today.AddDays(-5), 100m);

        await RebuildAsync(factory, scenario);

        // Anchored on the 500 that was measured, not on 1 000 - 200 carried across it.
        Assert.Equal(500m, await AccountsOnAsync(factory, scenario, measuredDay.AddDays(-1)));
        Assert.Equal(500m, await AccountsOnAsync(factory, scenario, measuredDay));
    }

    // The walk back subtracts BOOKED transactions only, so it has to start from booked money. The
    // preferred balance is interimAvailable, which already includes pending authorisations, and anchoring
    // there shifted every past day by the pending amount.
    [Fact]
    public async Task The_walk_back_starts_from_settled_money_not_from_pending_authorisations()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        // The provider reports both: 1 000 available (includes a 150 pending card authorisation) and 850
        // actually booked.
        // One capture with both types, exactly as a sync stores them.
        await AddProviderBalancesAsync(factory, scenario, (1000m, "interimAvailable"), (850m, "closingBooked"));
        await AddBookingAsync(factory, scenario, Today.AddDays(-2), 200m);

        await RebuildAsync(factory, scenario);

        // Today shows what the user sees now...
        Assert.Equal(1000m, await AccountsOnAsync(factory, scenario, Today));
        // ...and the past is reconstructed from the settled figure, not 150 too high.
        Assert.Equal(850m, await AccountsOnAsync(factory, scenario, Today.AddDays(-1)));
    }

    private static Task AddProviderBalancesAsync(
        BackendWebApplicationFactory factory,
        Scenario scenario,
        params (decimal Amount, string BalanceType)[] balances) =>
        factory.SeedAsync(async db =>
        {
            // A sync stamps every balance type of one account with an IDENTICAL CapturedAt, which is why
            // the type preference exists at all.
            var captured = DateTimeOffset.UtcNow.AddMinutes(5);
            foreach (var (amount, balanceType) in balances)
                db.BalanceSnapshots.Add(new BalanceSnapshot
                {
                    AccountId = scenario.Account,
                    Amount = amount,
                    Currency = "EUR",
                    BalanceType = balanceType,
                    CapturedAt = captured
                });
            await db.SaveChangesAsync();
        });

    private sealed record Scenario(Guid Owner, Guid Space, Guid Account);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var connection = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.Owner,
                EmailNormalized = $"{scenario.Owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "History owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "History", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connection,
                FullWorthSpaceId = scenario.Space,
                Provider = "test",
                InstitutionName = "Bank",
                Country = "DE",
                ProviderSessionId = $"history-{connection:N}"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.Account,
                FullWorthSpaceId = scenario.Space,
                BankConnectionId = connection,
                Provider = "test",
                IdentificationHash = $"history-{scenario.Account:N}",
                ProviderAccountId = $"history-{scenario.Account:N}",
                InstitutionName = "Bank",
                DisplayName = "Giro",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = scenario.Account,
                UserId = scenario.Owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.BalanceSnapshots.Add(new BalanceSnapshot
            {
                AccountId = scenario.Account,
                Amount = 1000m,
                Currency = "EUR",
                BalanceType = "closingBooked",
                CapturedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        return scenario;
    }

    /// <summary>A snapshot written on the day it describes — a measurement, not a reconstruction.</summary>
    private static Task AddMeasuredSnapshotAsync(
        BackendWebApplicationFactory factory, Scenario scenario, DateOnly day, decimal accounts) =>
        factory.SeedAsync(async db =>
        {
            db.NetWorthSnapshots.Add(new NetWorthSnapshot
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Date = day,
                Currency = "EUR",
                Accounts = accounts,
                Assets = 0m,
                Liabilities = 0m,
                NetWorth = accounts,
                CreatedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
        });

    private static Task AddBookingAsync(
        BackendWebApplicationFactory factory, Scenario scenario, DateOnly day, decimal amount) =>
        factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = scenario.Account,
                ExternalKey = $"history-{Guid.NewGuid():N}",
                Status = "BOOK",
                BookingDate = day,
                ValueDate = day,
                Amount = amount,
                Currency = "EUR",
                UseForBalanceHistory = true
            });
            await db.SaveChangesAsync();
        });

    private static async Task RebuildAsync(BackendWebApplicationFactory factory, Scenario scenario)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NetWorthSnapshotService>();
        // The full pass, which is what the worker runs on startup and what actually walks the history.
        await service.RebuildHistoryForUserAsync(scenario.Space, scenario.Owner, null, CancellationToken.None);
    }

    private static async Task<decimal?> AccountsOnAsync(
        BackendWebApplicationFactory factory, Scenario scenario, DateOnly day)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        return await db.NetWorthSnapshots.AsNoTracking()
            .Where(snapshot =>
                snapshot.FullWorthSpaceId == scenario.Space &&
                snapshot.UserId == scenario.Owner &&
                snapshot.Date == day &&
                snapshot.Currency == "EUR")
            .Select(snapshot => (decimal?)snapshot.Accounts)
            .SingleOrDefaultAsync();
    }
}
