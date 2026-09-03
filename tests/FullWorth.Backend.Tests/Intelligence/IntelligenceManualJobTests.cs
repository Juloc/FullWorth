using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class IntelligenceManualJobTests
{
    [Fact]
    public async Task Provider_smoke_job_creates_run_item_and_deduplicated_suggestion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var provider = new FakeProvider();
        var registry = new IntelligenceProviderRegistry([provider]);
        var store = new IntelligenceStore(db, FieldCipher.Null, registry);

        var credential = await store.CreateCredentialAsync(null, "fake", "test", "test-secret-value", CancellationToken.None);
        db.AiInstanceSettings.Add(new AiInstanceSettings
        {
            Enabled = true,
            Provider = "fake",
            CredentialId = credential.Id,
            DefaultTextModel = "fake-model",
            DefaultVisionModel = "fake-model"
        });
        await db.SaveChangesAsync();

        var service = new IntelligenceManualJobService(db, store, registry, new AiBudgetGuard(db));
        var first = await service.RunAsync(IntelligenceManualJobService.ProviderSmokeTestJobType, "same-request", CancellationToken.None);
        var second = await service.RunAsync(IntelligenceManualJobService.ProviderSmokeTestJobType, "same-request", CancellationToken.None);

        Assert.Equal(IntelligenceJobStatuses.Succeeded, first.Status);
        Assert.NotNull(first.RunId);
        Assert.NotNull(first.SuggestionId);
        Assert.Equal(first.JobId, second.JobId);
        Assert.Single(await db.AiRuns.ToListAsync());
        Assert.Single(await db.AiRunItems.ToListAsync());
        Assert.Single(await db.IntelligenceSuggestions.ToListAsync());

        var item = await db.AiRunItems.SingleAsync();
        Assert.Contains("FULLWORTH TEST GROCERIES", item.InputSummaryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("account", item.InputSummaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AiRunStatuses.Succeeded, item.Status);
    }

    [Fact]
    public async Task Provider_smoke_job_fails_without_enabled_ai_or_credential()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var registry = new IntelligenceProviderRegistry([new FakeProvider()]);
        var store = new IntelligenceStore(db, FieldCipher.Null, registry);
        db.AiInstanceSettings.Add(new AiInstanceSettings
        {
            Enabled = false,
            Provider = "fake",
            DefaultTextModel = "fake-model",
            DefaultVisionModel = "fake-model"
        });
        await db.SaveChangesAsync();

        var service = new IntelligenceManualJobService(db, store, registry, new AiBudgetGuard(db));
        var result = await service.RunAsync(IntelligenceManualJobService.ProviderSmokeTestJobType, null, CancellationToken.None);

        Assert.Equal(IntelligenceJobStatuses.Failed, result.Status);
        Assert.Equal("ai_disabled", result.ErrorCode);
        Assert.Empty(await db.AiRuns.ToListAsync());
    }

    [Fact]
    public async Task Provider_smoke_job_does_not_bypass_configured_budget_without_cost_estimate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var provider = new FakeProvider();
        var registry = new IntelligenceProviderRegistry([provider]);
        var store = new IntelligenceStore(db, FieldCipher.Null, registry);
        var credential = await store.CreateCredentialAsync(null, "fake", "test", "test-secret-value", CancellationToken.None);
        db.AiInstanceSettings.Add(new AiInstanceSettings
        {
            Enabled = true,
            Provider = "fake",
            CredentialId = credential.Id,
            DefaultTextModel = "fake-model",
            DefaultVisionModel = "fake-model",
            DailyBudgetEur = 1m
        });
        await db.SaveChangesAsync();

        var service = new IntelligenceManualJobService(db, store, registry, new AiBudgetGuard(db));
        var result = await service.RunAsync(IntelligenceManualJobService.ProviderSmokeTestJobType, "budgeted", CancellationToken.None);

        Assert.Equal(IntelligenceJobStatuses.Failed, result.Status);
        Assert.Equal("cost_estimate_required", result.ErrorCode);
        Assert.Empty(await db.AiRuns.ToListAsync());
    }

    private sealed class FakeProvider : IIntelligenceProvider
    {
        public IntelligenceProviderDescriptor Descriptor { get; } = new(
            "fake",
            IntelligenceProviderCapabilities.TextClassification | IntelligenceProviderCapabilities.StructuredExtraction,
            1024 * 1024,
            ReportsUsage: true);

        public Task<IntelligenceProviderTestResult> TestCredentialAsync(string credential, CancellationToken cancellationToken) =>
            Task.FromResult(new IntelligenceProviderTestResult(true));

        public Task<IntelligenceProviderResponse> ExecuteAsync(IntelligenceProviderRequest request, string credential, CancellationToken cancellationToken)
        {
            Assert.Contains("synthetic", request.SystemInstruction, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("test-secret-value", credential);
            return Task.FromResult(new IntelligenceProviderResponse(
                """{"decision":"accept_candidate","category":"food.groceries","confidenceBand":"high","evidenceSummary":"Synthetic grocery merchant name."}""",
                42,
                18,
                "req_test"));
        }
    }
}
