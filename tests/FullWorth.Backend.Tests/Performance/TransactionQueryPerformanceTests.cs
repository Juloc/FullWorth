using System.Diagnostics;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Performance;

/// <summary>
/// Transaction-query performance coverage. A moderate scoped-filter regression runs in the normal suite;
/// the 100k-row load harness stays opt-in via FULLWORTH_PERF=1. See docs/PERFORMANCE.md.
/// </summary>
public sealed class TransactionQueryPerformanceTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FULLWORTH_PERF") is "1" or "true";

    [Fact]
    public async Task ScopedTransactionFilters_StayResponsive_OverModerateDataset()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        var space = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var group = Guid.NewGuid();
        var groupedAccount = Guid.NewGuid();
        var otherAccount = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Performance user",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Scoped perf", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connection,
                FullWorthSpaceId = space,
                Provider = "perf",
                InstitutionName = "Perf",
                Country = "DE",
                ProviderSessionId = $"perf-scoped-{connection:N}",
                Status = "AUTHORIZED"
            });
            db.AccountGroups.Add(new AccountGroup { Id = group, FullWorthSpaceId = space, Name = "Daily", SortOrder = 0 });
            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = groupedAccount, FullWorthSpaceId = space, BankConnectionId = connection, GroupId = group,
                    Provider = "perf", IdentificationHash = $"perf-{groupedAccount:N}", ProviderAccountId = $"perf-{groupedAccount:N}",
                    InstitutionName = "Perf", DisplayName = "Grouped", Currency = "EUR"
                },
                new FinanceAccount
                {
                    Id = otherAccount, FullWorthSpaceId = space, BankConnectionId = connection,
                    Provider = "perf", IdentificationHash = $"perf-{otherAccount:N}", ProviderAccountId = $"perf-{otherAccount:N}",
                    InstitutionName = "Perf", DisplayName = "Other", Currency = "EUR"
                });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = groupedAccount, UserId = user, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = otherAccount, UserId = user, OwnershipType = AccountOwnershipTypes.Owner });
            db.Categories.AddRange(
                new FinanceCategory { Id = parent, FullWorthSpaceId = space, Key = "perf-parent", Name = "Food" },
                new FinanceCategory { Id = child, FullWorthSpaceId = space, ParentId = parent, Key = "perf-child", Name = "Groceries" });

            for (var i = 0; i < 4_000; i++)
            {
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = i < 3_000 ? groupedAccount : otherAccount,
                    CategoryId = i % 3 == 0 ? parent : child,
                    ExternalKey = $"perf-scoped-{i}",
                    Amount = -(20m + i % 200),
                    Currency = "EUR",
                    BookingDate = new DateOnly(2026, 1, 1).AddDays(i % 240),
                    NormalizedCounterparty = $"MERCHANT-{i % 40}",
                    Counterparty = $"Merchant {i % 40}",
                    Status = i % 17 == 0 ? "PDNG" : "BOOK",
                    RawJson = "{}"
                });
            }
            await db.SaveChangesAsync();
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<TransactionStore>();
        var query = new TransactionQuery(
            AccountId: null,
            CategoryId: parent,
            From: new DateOnly(2026, 2, 1),
            To: new DateOnly(2026, 8, 31),
            Direction: "expense",
            Query: null,
            IncludeIgnored: false,
            TransfersOnly: false,
            Sort: "date",
            Order: "desc",
            Offset: 0,
            Limit: 100,
            AccountGroupId: group,
            IncludeDescendants: true,
            Merchant: "MERCHANT-1",
            MinAmount: 25m,
            MaxAmount: 210m,
            RefundOnly: false,
            HasReceipt: null,
            Status: "booked",
            IgnoredOnly: false);

        var sw = Stopwatch.StartNew();
        var result = await store.SearchForUserAsync(user, space, query, CancellationToken.None);
        sw.Stop();

        var json = JsonSerializer.SerializeToElement(result);
        Assert.True(json.GetProperty("total").GetInt32() > 0);
        Assert.InRange(json.GetProperty("items").GetArrayLength(), 1, 100);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"scoped/descendant/merchant filter over 4,000 tx took {sw.ElapsedMilliseconds}ms (target <2000ms)");
    }

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
