using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Fx;

/// <summary>
/// Production hit `duplicate key value violates unique constraint "IX_FxRates_Date_Currency"` on
/// (2026-07-10, PHP). The read-then-insert in the refresh is not atomic — two overlapping runs (two
/// containers coexisting for a moment during a rolling restart, a startup pass overtaking the previous
/// one) both see the same gap and both try to fill it.
///
/// The error line understated it badly: every rate of a cycle went into ONE SaveChanges, so a single
/// duplicate rolled the whole batch back. The run stored NOTHING and the next attempt was 12 hours later —
/// which is exactly where "a required historical FX rate is missing" came from. The rates were fetched and
/// then thrown away because one of them already existed.
/// </summary>
public sealed class FxRateWriteTests
{
    private static readonly DateOnly Day = new(2026, 7, 10);

    [Fact]
    public async Task One_row_that_already_exists_no_longer_costs_the_whole_batch()
    {
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            db.FxRates.Add(new FxRate { Date = Day, Currency = "PHP", Rate = 65m });
            await db.SaveChangesAsync();
        });

        var stored = await WriteAsync(factory, [
            Rate("PHP", 65m),
            Rate("USD", 1.1m),
            Rate("IDR", 20000m)
        ]);

        // The duplicate is skipped, the other two are kept - before, all three were lost.
        Assert.Equal(2, stored);
        var currencies = await CurrenciesAsync(factory);
        Assert.Equal(["IDR", "PHP", "USD"], currencies);
    }

    [Fact]
    public async Task Writing_the_same_batch_twice_is_a_no_op_the_second_time()
    {
        using var factory = new BackendWebApplicationFactory();

        var first = await WriteAsync(factory, [Rate("PHP", 65m), Rate("USD", 1.1m)]);
        var second = await WriteAsync(factory, [Rate("PHP", 65m), Rate("USD", 1.1m)]);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        Assert.Equal(["PHP", "USD"], await CurrenciesAsync(factory));
    }

    // Two refreshes overlapping is the actual production scenario, and it must not raise at all.
    [Fact]
    public async Task Two_concurrent_writes_of_the_same_rates_both_succeed()
    {
        using var factory = new BackendWebApplicationFactory();
        var rates = new[] { Rate("PHP", 65m), Rate("USD", 1.1m), Rate("IDR", 20000m) };

        var results = await Task.WhenAll(
            WriteAsync(factory, rates),
            WriteAsync(factory, rates));

        // Whichever order they land in, together they insert each row exactly once.
        Assert.Equal(3, results.Sum());
        Assert.Equal(["IDR", "PHP", "USD"], await CurrenciesAsync(factory));
    }

    private static FxRate Rate(string currency, decimal rate) =>
        new() { Date = Day, Currency = currency, Rate = rate };

    private static async Task<int> WriteAsync(BackendWebApplicationFactory factory, IReadOnlyList<FxRate> rates)
    {
        // A fresh instance per row: the same tracked entity cannot be inserted by two contexts.
        var copies = rates
            .Select(rate => new FxRate { Date = rate.Date, Currency = rate.Currency, Rate = rate.Rate })
            .ToList();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        return await FxRateFetchWorker.InsertMissingAsync(db, copies, CancellationToken.None);
    }

    private static async Task<List<string>> CurrenciesAsync(BackendWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        return await db.FxRates.AsNoTracking()
            .Where(rate => rate.Date == Day)
            .OrderBy(rate => rate.Currency)
            .Select(rate => rate.Currency)
            .ToListAsync();
    }
}
