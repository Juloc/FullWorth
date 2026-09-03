using System.Diagnostics;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Performance;

/// <summary>
/// Opt-in load harness (Wave N8). Skipped unless FULLWORTH_PERF=1 so CI stays fast; run it locally
/// against a real Postgres to measure transaction search/filter/sort under a large dataset. Size via
/// FULLWORTH_PERF_TX (default 100000). See docs/PERFORMANCE.md.
/// </summary>
public sealed class TransactionQueryPerformanceTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FULLWORTH_PERF") is "1" or "true";

    [Fact]
    public async Task TransactionSearch_StaysResponsive_OverLargeDataset()
    {
        if (!Enabled) return; // opt-in: skipped in CI and normal runs

        var txCount = int.TryParse(Environment.GetEnvironmentVariable("FULLWORTH_PERF_TX"), out var n) ? n : 100_000;
        using var factory = new BackendWebApplicationFactory();
        var space = Guid.NewGuid();
        var account = Guid.NewGuid();
        var connection = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Perf", BaseCurrency = "EUR" });
            db.BankConnections.Add(new BankConnection { Id = connection, FullWorthSpaceId = space, Provider = "perf", InstitutionName = "Perf", Country = "DE", ProviderSessionId = $"perf-{connection:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = account, FullWorthSpaceId = space, BankConnectionId = connection, Provider = "perf", IdentificationHash = $"perf-{account:N}", ProviderAccountId = $"perf-{account:N}", InstitutionName = "Perf", DisplayName = "Perf", Currency = "EUR" });
            await db.SaveChangesAsync();
        });

        // Bulk-insert transactions in batches.
        var start = new DateOnly(2020, 1, 1);
        var inserted = 0;
        while (inserted < txCount)
        {
            var batch = Math.Min(5000, txCount - inserted);
            await factory.SeedAsync(db =>
            {
                for (var i = 0; i < batch; i++)
                {
                    var idx = inserted + i;
                    db.Transactions.Add(new FinanceTransaction
                    {
                        AccountId = account,
                        ExternalKey = $"perf-{idx}",
                        Amount = -(idx % 500 + 1) * 0.99m,
                        Currency = "EUR",
                        BookingDate = start.AddDays(idx % 2000),
                        NormalizedCounterparty = $"MERCHANT-{idx % 300}",
                        Counterparty = $"Merchant {idx % 300}",
                        RawJson = "{}"
                    });
                }
                return db.SaveChangesAsync();
            });
            inserted += batch;
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<FullWorth.Backend.Data.FullWorthDbContext>();

        var sw = Stopwatch.StartNew();
        var page = await db2.Transactions.AsNoTracking()
            .Where(t => t.AccountId == account && t.Amount < 0 && t.BookingDate >= new DateOnly(2021, 1, 1))
            .OrderByDescending(t => t.BookingDate)
            .Take(200)
            .ToListAsync();
        sw.Stop();

        Assert.Equal(200, page.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"filtered/sorted top-200 over {txCount} tx took {sw.ElapsedMilliseconds}ms (target <2000ms)");
    }
}
