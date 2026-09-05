using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Contracts;

/// <summary>
/// §7 server-side contract cost math: the list DTO exposes monthlyEquivalent and annualizedAmount computed
/// from the billing cycle × interval, so every client agrees. Runs on the in-memory SQLite model.
/// </summary>
public sealed class ContractCostMathTests
{
    [Theory]
    [InlineData("monthly", 1, 30, 30, 360)]
    [InlineData("yearly", 1, 120, 10, 120)]
    [InlineData("quarterly", 1, 30, 10, 120)]
    [InlineData("weekly", 1, 10, 43.33, 520)]
    [InlineData("monthly", 2, 30, 15, 180)] // every 2 months
    public async Task ListExposesMonthlyAndAnnualEquivalents(string cycle, int interval, decimal amount, decimal expectedMonthly, decimal expectedAnnual)
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var (space, user) = await SeedAsync(database, cycle, interval, amount);
        await using var db = database.CreateContext();
        var store = new ContractStore(db);

        var contracts = await store.ListForUserAsync(user, space, default);

        var view = Assert.Single(contracts);
        Assert.Equal(expectedMonthly, view.MonthlyEquivalent);
        Assert.Equal(expectedAnnual, view.AnnualizedAmount);
    }

    [Fact]
    public async Task Sort_ByMonthly_Descending()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var (space, user) = await SeedAsync(database, "monthly", 1, 30);
        await using var db = database.CreateContext();
        db.Contracts.Add(new RecurringContract { FullWorthSpaceId = space, Name = "Big", Amount = 100, Currency = "EUR", BillingCycle = "monthly", Interval = 1 });
        await db.SaveChangesAsync();
        var store = new ContractStore(db);

        var sorted = await store.ListForUserAsync(user, space, new ContractListQuery(Sort: "monthly", Order: "desc"), default);

        Assert.Equal("Big", sorted[0].Name);
        Assert.True(sorted[0].MonthlyEquivalent >= sorted[1].MonthlyEquivalent);
    }

    private static async Task<(Guid Space, Guid User)> SeedAsync(SqliteFullWorthDatabase database, string cycle, int interval, decimal amount)
    {
        var space = Guid.NewGuid();
        var user = Guid.NewGuid();
        await using var db = database.CreateContext();
        db.Users.Add(new FullWorthUser { Id = user, EmailNormalized = $"{user:N}@EX.COM".ToUpperInvariant(), DisplayName = "U", IsActive = true });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "S", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = user, Role = FullWorthSpaceRoles.Owner });
        db.Contracts.Add(new RecurringContract
        {
            FullWorthSpaceId = space,
            Name = "Streaming",
            Amount = amount,
            Currency = "EUR",
            BillingCycle = cycle,
            Interval = interval
        });
        await db.SaveChangesAsync();
        return (space, user);
    }
}
