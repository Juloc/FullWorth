using System.Text.Json;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// A historical value is stored AS OF ITS DATE. The past is never recomputed with today's rate, and a
/// day whose rate is missing is UNKNOWN rather than lower.
///
/// WealthHistoryCurrencyGapTests pins the null point for a single day with no rate at all. What is left
/// uncovered - and what an owner living with a rupiah account actually hits - is the interaction with
/// TIME: a rate table that is filled in from today backwards. Two failures are possible and neither had
/// a test:
/// <list type="bullet">
///   <item>today's rate is used for a day two months ago, so every past point silently moves whenever the
///         currency does. A measured past would be rewritten by a number nobody measured.</item>
///   <item>the reverse: a rate that arrives FOR that past date is refused, so a day that could be
///         resolved stays a hole forever.</item>
/// </list>
/// The stored snapshot rows are native amounts and must survive every read unchanged - a derived
/// conversion may never be written back over what was measured.
/// </summary>
public sealed class WealthHistoryHistoricalRateTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // Far enough back that no lookback window can reach today's fixing.
    private static readonly DateOnly PastDay = Today.AddDays(-60);

    private const decimal RateOnPastDay = 20_000m;
    private const decimal RateToday = 25_000m;

    // 100 EUR + 5 000 000 IDR at that day's 20 000 IDR per EUR = 350 EUR.
    private const decimal PastDayAtItsOwnRate = 350m;

    // The same day converted with today's 25 000 would be 300 EUR - a 50 EUR "loss" that never happened.
    private const decimal PastDayAtTodaysRate = 300m;

    /// <summary>
    /// Rule: a historical value is stored as of its date. A rate published today says nothing about a day
    /// two months ago, so that day stays unknown - reported as null, which the chart draws as a gap.
    /// Filling it in from today's rate would have rewritten a measured past with a guess; summing it
    /// without the rupiah account would have drawn a dip that never happened.
    /// </summary>
    [Fact]
    public async Task Todays_rate_neither_fills_in_a_past_day_nor_lowers_it()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, rateOnPastDay: false);
        using var client = factory.CreateClient();

        var point = await PastDayPointAsync(client, scenario);

        Assert.Equal(JsonValueKind.Null, point.GetProperty("netWorth").ValueKind);
        Assert.Equal(JsonValueKind.Null, point.GetProperty("accounts").ValueKind);
        Assert.False(point.GetProperty("isComplete").GetBoolean());
        Assert.Contains("IDR", point.GetProperty("missingCurrencies")
            .EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>
    /// Rule: the past is converted at ITS OWN date's rate. Both fixings are inside the snapshot window
    /// here, so nothing stops the wrong one from being picked except the rule itself.
    /// </summary>
    [Fact]
    public async Task A_past_day_is_converted_with_its_own_rate_not_todays()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, rateOnPastDay: true);
        using var client = factory.CreateClient();

        var point = await PastDayPointAsync(client, scenario);

        Assert.Equal(PastDayAtItsOwnRate, point.GetProperty("accounts").GetDecimal());
        Assert.Equal(PastDayAtItsOwnRate, point.GetProperty("netWorth").GetDecimal());
        Assert.NotEqual(PastDayAtTodaysRate, point.GetProperty("netWorth").GetDecimal());
        Assert.True(point.GetProperty("isComplete").GetBoolean());
    }

    /// <summary>
    /// The other side of the same rule: what makes a past day resolvable is a rate FOR THAT DATE, no
    /// matter when it was recorded. A rate backfilled for the past day turns the gap into the same figure
    /// the day would have had all along - it does not move the day to today's rate, and it does not stay
    /// a hole because it arrived late.
    /// </summary>
    [Fact]
    public async Task A_rate_backfilled_for_that_date_resolves_the_gap_at_that_dates_value()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, rateOnPastDay: false);
        using var client = factory.CreateClient();

        Assert.Equal(JsonValueKind.Null, (await PastDayPointAsync(client, scenario))
            .GetProperty("netWorth").ValueKind);

        await factory.SeedAsync(async db =>
        {
            CurrencyScenario.AddRate(db, PastDay, "IDR", RateOnPastDay);
            await db.SaveChangesAsync();
        });

        var point = await PastDayPointAsync(client, scenario);
        Assert.Equal(PastDayAtItsOwnRate, point.GetProperty("netWorth").GetDecimal());
        Assert.True(point.GetProperty("isComplete").GetBoolean());
    }

    /// <summary>
    /// Rule: a base-currency conversion is derived and never overwrites the original. Reading the trend
    /// converts every stored day; if any of that were written back, the native rupiah figure the snapshot
    /// measured would be gone and the next read would convert an already-converted number.
    /// </summary>
    [Fact]
    public async Task Reading_the_trend_leaves_the_stored_snapshot_in_its_own_currency()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, rateOnPastDay: true);
        using var client = factory.CreateClient();

        await PastDayPointAsync(client, scenario);
        await PastDayPointAsync(client, scenario);

        await factory.SeedAsync(async db =>
        {
            var rows = await db.NetWorthSnapshots.AsNoTracking()
                .Where(snapshot => snapshot.FullWorthSpaceId == scenario.Space && snapshot.Date == PastDay)
                .OrderBy(snapshot => snapshot.Currency)
                .Select(snapshot => new { snapshot.Currency, snapshot.Accounts, snapshot.NetWorth })
                .ToListAsync();

            Assert.Equal(
                [("EUR", 100m, 100m), ("IDR", 5_000_000m, 5_000_000m)],
                rows.Select(row => (row.Currency, row.Accounts, row.NetWorth)).ToArray());
        });
    }

    private sealed record Scenario(Guid Owner, Guid Space);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, bool rateOnPastDay)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            CurrencyScenario.AddOwnerAndSpace(db, owner, space, "EUR", "Trend");

            // One snapshot row per currency for the past day, exactly as the snapshot worker writes them.
            foreach (var (currency, amount) in new[] { ("EUR", 100m), ("IDR", 5_000_000m) })
                db.NetWorthSnapshots.Add(new NetWorthSnapshot
                {
                    FullWorthSpaceId = space,
                    UserId = owner,
                    Date = PastDay,
                    Currency = currency,
                    Accounts = amount,
                    Assets = 0m,
                    Liabilities = 0m,
                    NetWorth = amount
                });

            // A row for today as well, so the FX snapshot window the endpoint prepares spans both
            // fixings. Without it today's rate is not even loaded and the test could not tell the rule
            // apart from a window that happens to exclude it.
            db.NetWorthSnapshots.Add(new NetWorthSnapshot
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Date = Today,
                Currency = "EUR",
                Accounts = 0m,
                Assets = 0m,
                Liabilities = 0m,
                NetWorth = 0m
            });

            CurrencyScenario.AddRate(db, Today, "IDR", RateToday);
            if (rateOnPastDay) CurrencyScenario.AddRate(db, PastDay, "IDR", RateOnPastDay);
            await db.SaveChangesAsync();

            // Promote the past day's EUR row to a V2 carrier, which is the path every snapshot written
            // since the V2 upgrade takes.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "NetWorthSnapshots"
                SET "ManualAssets" = 0, "Investments" = 0, "Loans" = 0, "OtherLiabilities" = 0,
                    "ComponentCurrency" = 'EUR', "IsComplete" = true,
                    "MissingCurrenciesJson" = CAST('[]' AS jsonb)
                WHERE "FullWorthSpaceId" = {space} AND "Date" = {PastDay} AND "Currency" = 'EUR';
                """);
        });

        return new Scenario(owner, space);
    }

    private static async Task<JsonElement> PastDayPointAsync(HttpClient client, Scenario scenario)
    {
        var history = await CurrencyScenario.GetJsonAsync(
            client,
            scenario.Owner,
            $"/api/wealth/history?fullWorthSpaceId={scenario.Space:D}" +
            $"&from={PastDay:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&currency=EUR");
        return Assert.Single(
            history.EnumerateArray().Where(point =>
                point.GetProperty("date").GetString() == PastDay.ToString("yyyy-MM-dd")).ToArray());
    }
}
