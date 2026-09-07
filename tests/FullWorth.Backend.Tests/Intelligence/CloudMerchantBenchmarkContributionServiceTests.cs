using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudMerchantBenchmarkContributionServiceTests
{
    [Fact]
    public async Task Queues_previous_month_net_spend_with_only_pseudonymous_merchant_key()
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
        var connection = new BankConnection
        {
            FullWorthSpaceId = space.Id,
            InstitutionName = "Test Bank",
            Country = "DE"
        };
        var account = new FinanceAccount
        {
            FullWorthSpaceId = space.Id,
            BankConnectionId = connection.Id,
            IdentificationHash = "test-account",
            InstitutionName = "Test Bank",
            DisplayName = "Giro",
            Currency = "EUR"
        };
        var purchase = new FinanceTransaction
        {
            AccountId = account.Id,
            ExternalKey = "purchase",
            BookingDate = new DateOnly(2026, 8, 5),
            Amount = -100m,
            Currency = "EUR",
            Counterparty = "REWE Markt",
            NormalizedCounterparty = "REWE",
            CategorizationSource = "manual"
        };
        var refund = new FinanceTransaction
        {
            AccountId = account.Id,
            ExternalKey = "refund",
            BookingDate = new DateOnly(2026, 8, 10),
            Amount = 20m,
            Currency = "EUR",
            Counterparty = "REWE Erstattung",
            NormalizedCounterparty = "REWE REFUND",
            RefundOfTransactionId = purchase.Id
        };
        var currentMonth = new FinanceTransaction
        {
            AccountId = account.Id,
            ExternalKey = "september",
            BookingDate = new DateOnly(2026, 9, 2),
            Amount = -999m,
            Currency = "EUR",
            Counterparty = "REWE",
            NormalizedCounterparty = "REWE"
        };

        financeDb.FullWorthSpaces.Add(space);
        financeDb.BankConnections.Add(connection);
        financeDb.Accounts.Add(account);
        financeDb.Transactions.AddRange(purchase, refund, currentMonth);
        await financeDb.SaveChangesAsync();

        const string legacyCanonicalKey = "REWE\u001fexpense\u001fDE";
        intelligenceDb.OfficialMerchantMappings.Add(new OfficialMerchantMapping
        {
            PackId = "pack",
            PackVersion = "1",
            AliasKey = "REWE",
            Direction = "expense",
            CanonicalMerchantKey = legacyCanonicalKey,
            CanonicalName = "REWE",
            CategoryKey = "food.groceries",
            Country = "DE",
            Confidence = 0.99m
        });
        await intelligenceDb.SaveChangesAsync();

        var cloudState = new CloudIntelligenceStateService(intelligenceDb);
        await cloudState.EnableAsync(
            Guid.NewGuid(),
            new EnableCloudIntelligenceRequest(
                CloudIntelligencePolicy.CurrentVersion,
                "de",
                "test"),
            CancellationToken.None);

        var service = new CloudMerchantBenchmarkContributionService(
            financeDb,
            intelligenceDb,
            cloudState,
            new CloudOperationalRegistryResolver(intelligenceDb));

        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, await service.QueuePreviousMonthAsync(now, CancellationToken.None));

        var row = await intelligenceDb.CloudSubmissionOutbox.SingleAsync();
        using var doc = JsonDocument.Parse(row.PayloadJson);
        var root = doc.RootElement;

        Assert.Equal(CloudMerchantBenchmarkContributionService.MetricKey,
            root.GetProperty("metricKey").GetString());
        Assert.Equal(80m, root.GetProperty("value").GetDecimal());
        Assert.Equal("EUR", root.GetProperty("currency").GetString());
        Assert.Equal("DE", root.GetProperty("country").GetString());
        Assert.Equal("2026-08", root.GetProperty("observedMonth").GetString());
        Assert.Equal(
            CloudBenchmarkEntityKeys.ForMerchant(legacyCanonicalKey),
            root.GetProperty("entityKey").GetString());
        Assert.DoesNotContain("REWE", row.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(legacyCanonicalKey, row.PayloadJson, StringComparison.Ordinal);

        // Unchanged completed-month data is not submitted again.
        Assert.Equal(0, await service.QueuePreviousMonthAsync(now, CancellationToken.None));
        Assert.Equal(1, await intelligenceDb.CloudSubmissionOutbox.CountAsync());

        // A late correction creates a new content-addressed revision for the same month.
        purchase.Amount = -120m;
        financeDb.Transactions.Update(purchase);
        await financeDb.SaveChangesAsync();

        Assert.Equal(1, await service.QueuePreviousMonthAsync(now, CancellationToken.None));
        Assert.Equal(2, await intelligenceDb.CloudSubmissionOutbox.CountAsync());
    }

    [Fact]
    public void Merchant_benchmark_entity_key_is_semantic_and_does_not_expose_legacy_key()
    {
        const string legacy = "REWE\u001fexpense\u001fDE";
        var key = CloudBenchmarkEntityKeys.ForMerchant(legacy);

        Assert.StartsWith("merchant.", key, StringComparison.Ordinal);
        Assert.DoesNotContain("REWE", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\u001f", key, StringComparison.Ordinal);
        Assert.All(key, ch => Assert.True(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'));
    }
}
