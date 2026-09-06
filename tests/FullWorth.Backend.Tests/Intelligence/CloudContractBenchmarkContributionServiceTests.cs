using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudContractBenchmarkContributionServiceTests
{
    [Fact]
    public async Task Queues_one_privacy_safe_median_observation_per_metric_currency()
    {
        await using var financeConnection = new SqliteConnection("Data Source=:memory:");
        await financeConnection.OpenAsync();
        await using var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
        await intelligenceConnection.OpenAsync();

        var financeOptions = new DbContextOptionsBuilder<FullWorthDbContext>()
            .UseSqlite(financeConnection)
            .Options;
        var intelligenceOptions = new DbContextOptionsBuilder<IntelligenceDbContext>()
            .UseSqlite(intelligenceConnection)
            .Options;

        await using var financeDb = new FullWorthDbContext(financeOptions);
        await using var intelligenceDb = new IntelligenceDbContext(intelligenceOptions);
        await financeDb.Database.EnsureCreatedAsync();
        await intelligenceDb.Database.EnsureCreatedAsync();

        var space = new FullWorthSpace { Name = "Household", BaseCurrency = "EUR" };
        var electricity = new FinanceCategory
        {
            FullWorthSpaceId = space.Id,
            Key = "housing.electricity",
            Name = "Strom",
            IsSystem = true
        };
        financeDb.FullWorthSpaces.Add(space);
        financeDb.Categories.Add(electricity);
        financeDb.BankConnections.Add(new BankConnection
        {
            FullWorthSpaceId = space.Id,
            InstitutionName = "Test Bank",
            Country = "DE"
        });
        financeDb.Contracts.AddRange(
            new RecurringContract
            {
                FullWorthSpaceId = space.Id,
                Name = "Provider A",
                ProviderName = "Provider A",
                CategoryId = electricity.Id,
                Amount = 30m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                IsActive = true
            },
            new RecurringContract
            {
                FullWorthSpaceId = space.Id,
                Name = "Provider B",
                ProviderName = "Provider B",
                CategoryId = electricity.Id,
                Amount = 150m,
                Currency = "EUR",
                BillingCycle = "quarterly",
                Interval = 1,
                IsActive = true
            });
        await financeDb.SaveChangesAsync();

        var state = new CloudIntelligenceStateService(intelligenceDb);
        await state.EnableAsync(
            Guid.NewGuid(),
            new EnableCloudIntelligenceRequest(
                CloudIntelligencePolicy.CurrentVersion,
                "de",
                "test"),
            CancellationToken.None);

        var service = new CloudContractBenchmarkContributionService(
            financeDb, intelligenceDb, state);
        var now = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);

        var queued = await service.QueueCurrentAsync(now, CancellationToken.None);

        Assert.Equal(1, queued);
        var row = await intelligenceDb.CloudSubmissionOutbox.SingleAsync();
        Assert.Equal("benchmark_observation", row.EventType);
        Assert.Equal(CloudSubmissionStatuses.Queued, row.Status);

        using var doc = JsonDocument.Parse(row.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal("contract.energy.monthly_cost", root.GetProperty("metricKey").GetString());
        Assert.Equal(40m, root.GetProperty("value").GetDecimal());
        Assert.Equal("EUR", root.GetProperty("currency").GetString());
        Assert.Equal("DE", root.GetProperty("country").GetString());
        Assert.Equal("2026-09", root.GetProperty("observedMonth").GetString());

        var names = root.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "country", "currency", "metricKey", "observedMonth", "value" },
            names);
        Assert.DoesNotContain("Provider A", row.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider B", row.PayloadJson, StringComparison.Ordinal);

        // Same-day rerun is idempotent and does not create another outbox event.
        Assert.Equal(0, await service.QueueCurrentAsync(now, CancellationToken.None));
        Assert.Equal(1, await intelligenceDb.CloudSubmissionOutbox.CountAsync());
    }

    [Theory]
    [InlineData("housing.electricity", "contract.energy.monthly_cost")]
    [InlineData("housing.internet", "contract.internet.monthly_cost")]
    [InlineData("insurance", "contract.insurance.monthly_cost")]
    [InlineData("insurance.health", "contract.insurance.health.monthly_cost")]
    [InlineData("insurance.vehicle", "contract.insurance.monthly_cost")]
    [InlineData("food.groceries", null)]
    public void Only_structured_supported_categories_become_contract_metrics(
        string categoryKey,
        string? expected)
    {
        Assert.Equal(
            expected,
            CloudContractBenchmarkContributionService.MetricForCategory(categoryKey));
    }
}
