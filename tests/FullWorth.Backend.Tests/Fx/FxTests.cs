using System.Text.Json;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Fx;

public sealed class CurrencyConverterTests
{
    private static async Task SeedAsync(SqliteFullWorthDatabase database, params (string d, string cur, decimal rate)[] rates)
    {
        await using var db = database.CreateContext();
        foreach (var (d, cur, rate) in rates)
            db.FxRates.Add(new FxRate { Date = DateOnly.Parse(d), Currency = cur, Rate = rate });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ConvertsToEurBaseViaEcbNativeRates()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await SeedAsync(database, ("2026-08-28", "USD", 1.10m), ("2026-08-28", "GBP", 0.85m));
        await using var db = database.CreateContext();
        var snap = await new CurrencyConverter(db).PrepareLatestAsync("EUR", new DateOnly(2026, 8, 28), CancellationToken.None);

        Assert.Equal(100m, snap.ToBaseOn(110m, "USD", new DateOnly(2026, 8, 28)));   // 110 USD / 1.10 = 100 EUR
        Assert.Equal(100m, snap.ToBaseOn(85m, "GBP", new DateOnly(2026, 8, 28)));    // 85 GBP / 0.85 = 100 EUR
        Assert.Equal(42m, snap.ToBaseOn(42m, "EUR", new DateOnly(2026, 8, 28)));     // base passthrough
        Assert.Null(snap.ToBaseOn(50m, "CHF", new DateOnly(2026, 8, 28)));           // no rate -> missing (never 1:1)
    }

    [Fact]
    public async Task ConvertsToNonEurBaseThroughEurCross()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await SeedAsync(database, ("2026-08-28", "USD", 1.10m));
        await using var db = database.CreateContext();
        var snap = await new CurrencyConverter(db).PrepareLatestAsync("USD", new DateOnly(2026, 8, 28), CancellationToken.None);

        Assert.Equal(110m, snap.ToBaseOn(100m, "EUR", new DateOnly(2026, 8, 28)));   // 100 EUR * 1.10 = 110 USD
        Assert.Equal(200m, snap.ToBaseOn(200m, "USD", new DateOnly(2026, 8, 28)));   // base passthrough
    }

    [Fact]
    public async Task UsesLatestFixingWithinLookbackButNotBeyond()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await SeedAsync(database, ("2026-08-28", "USD", 1.10m));
        await using var db = database.CreateContext();
        // Window must include the lookback source; prepare across a range that covers both query dates.
        var snap = await new CurrencyConverter(db).PrepareAsync("EUR", new DateOnly(2026, 8, 28), new DateOnly(2026, 9, 30), CancellationToken.None);

        // Fri fixing used for the following (unfixed) weekend — within the 14-day lookback.
        Assert.Equal(100m, snap.ToBaseOn(110m, "USD", new DateOnly(2026, 8, 30)));
        // 3+ weeks later with no newer fixing is beyond the lookback -> missing.
        Assert.Null(snap.ToBaseOn(110m, "USD", new DateOnly(2026, 9, 20)));
    }
}

public sealed class FxRateProviderParseTests
{
    [Fact]
    public void ParsesARangeResponseIntoPerDayPerCurrencyRates()
    {
        using var doc = JsonDocument.Parse("""
        {"amount":1,"base":"EUR","start_date":"2026-08-27","end_date":"2026-08-28",
         "rates":{"2026-08-27":{"USD":1.09},"2026-08-28":{"USD":1.10,"GBP":0.85}}}
        """);
        var rows = FxRateProvider.Parse(doc.RootElement);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Date == new DateOnly(2026, 8, 28) && r.Currency == "USD" && r.Rate == 1.10m);
        Assert.Contains(rows, r => r.Date == new DateOnly(2026, 8, 28) && r.Currency == "GBP" && r.Rate == 0.85m);
        Assert.Contains(rows, r => r.Date == new DateOnly(2026, 8, 27) && r.Currency == "USD" && r.Rate == 1.09m);
    }

    [Fact]
    public void ParsesASingleDayResponse()
    {
        using var doc = JsonDocument.Parse("""{"amount":1,"base":"EUR","date":"2026-08-28","rates":{"USD":1.10}}""");
        var rows = FxRateProvider.Parse(doc.RootElement);
        var row = Assert.Single(rows);
        Assert.Equal(new DateOnly(2026, 8, 28), row.Date);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(1.10m, row.Rate);
    }
}
