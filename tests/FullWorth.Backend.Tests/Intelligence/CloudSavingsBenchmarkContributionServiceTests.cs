using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudSavingsBenchmarkContributionServiceTests
{
    [Fact]
    public async Task Completed_month_queues_only_rate_and_coarse_dimensions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var space = await fixture.AddSpaceAsync("EUR", "DE");
        var account = await fixture.AddAccountAsync(space.Id, "EUR");

        var expense = new FinanceTransaction
        {
            AccountId = account.Id,
            ExternalKey = "expense",
            Status = "BOOK",
            BookingDate = new DateOnly(2026, 8, 10),
            Amount = -2000m,
            Currency = "EUR"
        };
        fixture.Finance.Transactions.AddRange(
            new FinanceTransaction
            {
                AccountId = account.Id,
                ExternalKey = "income",
                Status = "BOOK",
                BookingDate = new DateOnly(2026, 8, 1),
                Amount = 3000m,
                Currency = "EUR"
            },
            expense,
            new FinanceTransaction
            {
                AccountId = account.Id,
                ExternalKey = "refund",
                Status = "BOOK",
                BookingDate = new DateOnly(2026, 8, 20),
                Amount = 100m,
                Currency = "EUR",
                RefundOfTransactionId = expense.Id
            });
        await fixture.Finance.SaveChangesAsync();

        var service = fixture.CreateService();
        var now = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);

        var snapshot = await service.ComputeSpaceAsync(space.Id, now, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(0.3667m, snapshot!.SavingsRate);
        Assert.Equal(3000m, snapshot.MonthlyIncome);
        Assert.Equal("25k_50k", snapshot.IncomeBand);
        Assert.Equal("DE", snapshot.Country);
        Assert.Equal("2026-08", snapshot.ObservedMonth);

        Assert.Equal(1, await service.QueueCurrentAsync(now, CancellationToken.None));
        var row = await fixture.Intelligence.CloudSubmissionOutbox.SingleAsync();
        Assert.Equal("benchmark_observation", row.EventType);

        using var doc = JsonDocument.Parse(row.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal("savings.rate", root.GetProperty("metricKey").GetString());
        Assert.Equal(0.3667m, root.GetProperty("value").GetDecimal());
        Assert.Equal("DE", root.GetProperty("country").GetString());
        Assert.Equal("25k_50k", root.GetProperty("incomeBand").GetString());
        Assert.Equal("2026-08", root.GetProperty("observedMonth").GetString());

        var names = root.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "country", "incomeBand", "metricKey", "observedMonth", "value" },
            names);

        Assert.Equal(0, await service.QueueCurrentAsync(now, CancellationToken.None));
        Assert.Equal(1, await fixture.Intelligence.CloudSubmissionOutbox.CountAsync());
    }

    [Fact]
    public async Task Missing_fx_rate_skips_space_instead_of_uploading_incomplete_rate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var space = await fixture.AddSpaceAsync("EUR", "DE");
        var account = await fixture.AddAccountAsync(space.Id, "USD");
        fixture.Finance.Transactions.AddRange(
            new FinanceTransaction
            {
                AccountId = account.Id,
                ExternalKey = "income-usd",
                Status = "BOOK",
                BookingDate = new DateOnly(2026, 8, 1),
                Amount = 3000m,
                Currency = "USD"
            },
            new FinanceTransaction
            {
                AccountId = account.Id,
                ExternalKey = "expense-usd",
                Status = "BOOK",
                BookingDate = new DateOnly(2026, 8, 10),
                Amount = -1500m,
                Currency = "USD"
            });
        await fixture.Finance.SaveChangesAsync();

        var service = fixture.CreateService();
        var now = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);

        Assert.Null(await service.ComputeSpaceAsync(space.Id, now, CancellationToken.None));
        Assert.Equal(0, await service.QueueCurrentAsync(now, CancellationToken.None));
        Assert.Empty(await fixture.Intelligence.CloudSubmissionOutbox.ToListAsync());
    }

    [Theory]
    [InlineData(24000, "lt_25k")]
    [InlineData(25000, "25k_50k")]
    [InlineData(50000, "50k_75k")]
    [InlineData(75000, "75k_100k")]
    [InlineData(100000, "100k_150k")]
    [InlineData(150000, "150k_plus")]
    public void Income_band_is_coarse_and_boundary_stable(decimal income, string expected)
    {
        Assert.Equal(expected, CloudSavingsBenchmarkContributionService.IncomeBand(income));
    }

    private sealed class Fixture(
        SqliteConnection financeConnection,
        SqliteConnection intelligenceConnection,
        FullWorthDbContext finance,
        IntelligenceDbContext intelligence,
        CloudIntelligenceStateService cloudState) : IAsyncDisposable
    {
        public FullWorthDbContext Finance { get; } = finance;
        public IntelligenceDbContext Intelligence { get; } = intelligence;

        public static async Task<Fixture> CreateAsync()
        {
            var financeConnection = new SqliteConnection("Data Source=:memory:");
            await financeConnection.OpenAsync();
            var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
            await intelligenceConnection.OpenAsync();

            var finance = new FullWorthDbContext(
                new DbContextOptionsBuilder<FullWorthDbContext>()
                    .UseSqlite(financeConnection)
                    .Options);
            var intelligence = new IntelligenceDbContext(
                new DbContextOptionsBuilder<IntelligenceDbContext>()
                    .UseSqlite(intelligenceConnection)
                    .Options);
            await finance.Database.EnsureCreatedAsync();
            await intelligence.Database.EnsureCreatedAsync();

            var cloudState = new CloudIntelligenceStateService(intelligence);
            await cloudState.EnableAsync(
                Guid.NewGuid(),
                new EnableCloudIntelligenceRequest(
                    CloudIntelligencePolicy.CurrentVersion,
                    "de",
                    "test"),
                CancellationToken.None);

            return new Fixture(
                financeConnection,
                intelligenceConnection,
                finance,
                intelligence,
                cloudState);
        }

        public async Task<FullWorthSpace> AddSpaceAsync(string baseCurrency, string country)
        {
            var space = new FullWorthSpace
            {
                Name = "Household",
                BaseCurrency = baseCurrency
            };
            Finance.FullWorthSpaces.Add(space);
            Finance.BankConnections.Add(new BankConnection
            {
                FullWorthSpaceId = space.Id,
                InstitutionName = "Test Bank",
                Country = country
            });
            await Finance.SaveChangesAsync();
            return space;
        }

        public async Task<FinanceAccount> AddAccountAsync(Guid spaceId, string currency)
        {
            var account = new FinanceAccount
            {
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = Guid.NewGuid().ToString("N"),
                ProviderAccountId = Guid.NewGuid().ToString("N"),
                InstitutionName = "Test",
                DisplayName = "Test",
                Currency = currency
            };
            Finance.Accounts.Add(account);
            await Finance.SaveChangesAsync();
            return account;
        }

        public CloudSavingsBenchmarkContributionService CreateService() =>
            new(
                Finance,
                Intelligence,
                cloudState,
                new CurrencyConverter(Finance));

        public async ValueTask DisposeAsync()
        {
            await Finance.DisposeAsync();
            await Intelligence.DisposeAsync();
            await financeConnection.DisposeAsync();
            await intelligenceConnection.DisposeAsync();
        }
    }
}
