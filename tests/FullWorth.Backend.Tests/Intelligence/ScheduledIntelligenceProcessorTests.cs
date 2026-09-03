using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class ScheduledIntelligenceProcessorTests
{
    [Fact]
    public async Task Daily_scan_creates_reviewable_suggestion_without_mutating_transaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Provider.OutputJson = """
{"suggestions":[{"merchant":"REWE","direction":"expense","categoryKey":"food.groceries","confidenceBand":"high","evidenceSummary":"Known grocery merchant."}]}
""";

        await fixture.Processor.ProcessAsync(fixture.Job, CancellationToken.None);

        Assert.Equal(1, fixture.Provider.ExecuteCount);
        var suggestion = await fixture.IntelligenceDb.IntelligenceSuggestions.SingleAsync();
        Assert.Equal("merchant-category", suggestion.Type);
        Assert.Equal("REWE", suggestion.SubjectId);
        Assert.Equal(IntelligenceSuggestionStatuses.Pending, suggestion.Status);
        Assert.Equal(0.90m, suggestion.Confidence);

        var transaction = await fixture.FinanceDb.Transactions.AsNoTracking().SingleAsync();
        Assert.Null(transaction.CategoryId);
        Assert.Equal("none", transaction.CategorizationSource);
        Assert.Equal(IntelligenceJobStatuses.Succeeded,
            (await fixture.IntelligenceDb.IntelligenceJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await fixture.IntelligenceDb.AiRuns.AsNoTracking().ToListAsync());
        Assert.Single(await fixture.IntelligenceDb.IntelligenceWatermarks.AsNoTracking().ToListAsync());
        Assert.Single(await fixture.IntelligenceDb.IntelligenceDigests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Known_learned_mapping_is_excluded_before_provider_call()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.IntelligenceDb.LearnedMerchantMappings.Add(new LearnedMerchantMapping
        {
            FullWorthSpaceId = fixture.Space.Id,
            CreatedByUserId = Guid.NewGuid(),
            NormalizedCounterparty = "REWE",
            Direction = "expense",
            CategoryId = fixture.Category.Id,
            Source = "user-confirmed",
            IsActive = true
        });
        await fixture.IntelligenceDb.SaveChangesAsync();

        await fixture.Processor.ProcessAsync(fixture.Job, CancellationToken.None);

        Assert.Equal(0, fixture.Provider.ExecuteCount);
        Assert.Empty(await fixture.IntelligenceDb.IntelligenceSuggestions.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.IntelligenceDb.AiRuns.AsNoTracking().ToListAsync());
        Assert.Equal(IntelligenceJobStatuses.Succeeded,
            (await fixture.IntelligenceDb.IntelligenceJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await fixture.IntelligenceDb.IntelligenceDigests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Budget_without_cost_estimate_defers_before_provider_call()
    {
        await using var fixture = await Fixture.CreateAsync(dailyBudgetEur: 1m);

        await fixture.Processor.ProcessAsync(fixture.Job, CancellationToken.None);

        Assert.Equal(0, fixture.Provider.ExecuteCount);
        Assert.Empty(await fixture.IntelligenceDb.AiRuns.AsNoTracking().ToListAsync());
        var job = await fixture.IntelligenceDb.IntelligenceJobs.AsNoTracking().SingleAsync();
        Assert.Equal(IntelligenceJobStatuses.Deferred, job.Status);
        Assert.Equal("cost_estimate_required", job.ErrorCode);
        Assert.NotNull(job.NextRetryAt);
    }

    [Fact]
    public async Task Provider_cannot_invent_category_key()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Provider.OutputJson = """
{"suggestions":[{"merchant":"REWE","direction":"expense","categoryKey":"invented.category","confidenceBand":"high","evidenceSummary":"Invalid invented category."}]}
""";

        await fixture.Processor.ProcessAsync(fixture.Job, CancellationToken.None);

        Assert.Equal(1, fixture.Provider.ExecuteCount);
        Assert.Empty(await fixture.IntelligenceDb.IntelligenceSuggestions.AsNoTracking().ToListAsync());
        Assert.Equal(IntelligenceJobStatuses.Succeeded,
            (await fixture.IntelligenceDb.IntelligenceJobs.AsNoTracking().SingleAsync()).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection intelligenceConnection;
        private readonly SqliteConnection financeConnection;

        private Fixture(
            SqliteConnection intelligenceConnection,
            SqliteConnection financeConnection,
            IntelligenceDbContext intelligenceDb,
            FullWorthDbContext financeDb,
            FakeProvider provider,
            ScheduledIntelligenceJobProcessor processor,
            IntelligenceJob job,
            FullWorthSpace space,
            FinanceCategory category)
        {
            this.intelligenceConnection = intelligenceConnection;
            this.financeConnection = financeConnection;
            IntelligenceDb = intelligenceDb;
            FinanceDb = financeDb;
            Provider = provider;
            Processor = processor;
            Job = job;
            Space = space;
            Category = category;
        }

        public IntelligenceDbContext IntelligenceDb { get; }
        public FullWorthDbContext FinanceDb { get; }
        public FakeProvider Provider { get; }
        public ScheduledIntelligenceJobProcessor Processor { get; }
        public IntelligenceJob Job { get; }
        public FullWorthSpace Space { get; }
        public FinanceCategory Category { get; }

        public static async Task<Fixture> CreateAsync(decimal? dailyBudgetEur = null)
        {
            var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
            var financeConnection = new SqliteConnection("Data Source=:memory:");
            await intelligenceConnection.OpenAsync();
            await financeConnection.OpenAsync();

            var intelligenceOptions = new DbContextOptionsBuilder<IntelligenceDbContext>()
                .UseSqlite(intelligenceConnection).Options;
            var financeOptions = new DbContextOptionsBuilder<FullWorthDbContext>()
                .UseSqlite(financeConnection).Options;
            var intelligenceDb = new IntelligenceDbContext(intelligenceOptions);
            var financeDb = new FullWorthDbContext(financeOptions);
            await intelligenceDb.Database.EnsureCreatedAsync();
            await financeDb.Database.EnsureCreatedAsync();

            // EnsureCreated applies the model's HasData seed, which includes the default finance space.
            // The daily scan enumerates every finance space, so drop the seed and run against exactly
            // the single scenario space added below (otherwise the empty default space yields a second
            // watermark/digest).
            financeDb.FullWorthSpaces.RemoveRange(await financeDb.FullWorthSpaces.ToListAsync());
            await financeDb.SaveChangesAsync();

            var space = new FullWorthSpace { Name = "Test", BaseCurrency = "EUR" };
            var category = new FinanceCategory
            {
                FullWorthSpaceId = space.Id,
                Key = "food.groceries",
                Name = "Lebensmittel",
                IsSystem = true,
                IsArchived = false
            };
            var account = new FinanceAccount
            {
                FullWorthSpaceId = space.Id,
                Provider = "manual",
                IdentificationHash = "test-account",
                ProviderAccountId = "test-account",
                InstitutionName = "Test",
                DisplayName = "Testkonto",
                Currency = "EUR"
            };
            var now = DateTimeOffset.UtcNow;
            var transaction = new FinanceTransaction
            {
                AccountId = account.Id,
                ExternalKey = "test:rewe:1",
                Amount = -42.50m,
                Currency = "EUR",
                Counterparty = "REWE Markt GmbH",
                NormalizedCounterparty = "REWE",
                CategorizationSource = "none",
                FirstSeenAt = now.AddMinutes(-5),
                UpdatedAt = now.AddMinutes(-5)
            };
            financeDb.FullWorthSpaces.Add(space);
            financeDb.Categories.Add(category);
            financeDb.Accounts.Add(account);
            financeDb.Transactions.Add(transaction);
            await financeDb.SaveChangesAsync();

            var provider = new FakeProvider();
            var registry = new IntelligenceProviderRegistry([provider]);
            var store = new IntelligenceStore(intelligenceDb, FieldCipher.Null, registry);
            var credential = await store.CreateCredentialAsync(null, "fake", "test", "test-secret-value", CancellationToken.None);
            intelligenceDb.AiInstanceSettings.Add(new AiInstanceSettings
            {
                Enabled = true,
                Provider = "fake",
                CredentialId = credential.Id,
                DefaultTextModel = "fake-model",
                DefaultVisionModel = "fake-model",
                DailyBudgetEur = dailyBudgetEur,
                MerchantAiEnabled = true,
                CategoryAiEnabled = true,
                DailyScanEnabled = true
            });
            var job = new IntelligenceJob
            {
                Type = ScheduledIntelligenceJobTypes.DailyIncremental,
                ScopeKey = "instance",
                ScheduledFor = now,
                IdempotencyKey = $"test:{Guid.NewGuid():N}",
                Status = IntelligenceJobStatuses.Running,
                StartedAt = now
            };
            intelligenceDb.IntelligenceJobs.Add(job);
            await intelligenceDb.SaveChangesAsync();

            var configuration = new ConfigurationBuilder().Build();
            var budgetGuard = new AiBudgetGuard(intelligenceDb);
            var costEstimator = new AiCostEstimator(configuration);
            var adapters = new ScheduledDomainIntelligenceAdapters(
                intelligenceDb,
                financeDb,
                store,
                registry,
                budgetGuard,
                costEstimator);
            var processor = new ScheduledIntelligenceJobProcessor(
                intelligenceDb,
                financeDb,
                store,
                registry,
                new IntelligenceWatermarkStore(intelligenceDb),
                budgetGuard,
                costEstimator,
                adapters,
                new IntelligenceDigestService(intelligenceDb, financeDb),
                NullLogger<ScheduledIntelligenceJobProcessor>.Instance);

            return new Fixture(
                intelligenceConnection,
                financeConnection,
                intelligenceDb,
                financeDb,
                provider,
                processor,
                job,
                space,
                category);
        }

        public async ValueTask DisposeAsync()
        {
            await IntelligenceDb.DisposeAsync();
            await FinanceDb.DisposeAsync();
            await intelligenceConnection.DisposeAsync();
            await financeConnection.DisposeAsync();
        }
    }

    private sealed class FakeProvider : IIntelligenceProvider
    {
        public int ExecuteCount { get; private set; }
        public string OutputJson { get; set; } = """
{"suggestions":[{"merchant":"REWE","direction":"expense","categoryKey":"food.groceries","confidenceBand":"high","evidenceSummary":"Known grocery merchant."}]}
""";

        public IntelligenceProviderDescriptor Descriptor { get; } = new(
            "fake",
            IntelligenceProviderCapabilities.TextClassification | IntelligenceProviderCapabilities.StructuredExtraction,
            1024 * 1024,
            ReportsUsage: true);

        public Task<IntelligenceProviderTestResult> TestCredentialAsync(string credential, CancellationToken cancellationToken) =>
            Task.FromResult(new IntelligenceProviderTestResult(true));

        public Task<IntelligenceProviderResponse> ExecuteAsync(
            IntelligenceProviderRequest request,
            string credential,
            CancellationToken cancellationToken)
        {
            ExecuteCount += 1;
            Assert.Equal("test-secret-value", credential);
            Assert.Contains("REWE", request.InputJson, StringComparison.Ordinal);
            Assert.DoesNotContain("42.5", request.InputJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Testkonto", request.InputJson, StringComparison.Ordinal);
            Assert.DoesNotContain("ExternalKey", request.InputJson, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new IntelligenceProviderResponse(OutputJson, 30, 20, "req_fake"));
        }
    }
}
