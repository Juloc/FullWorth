using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class IntelligenceFoundationTests
{
    [Fact]
    public void Provider_registry_resolves_declared_provider()
    {
        var provider = new FakeProvider();
        var registry = new IntelligenceProviderRegistry([provider]);

        Assert.Same(provider, registry.GetRequired("fake"));
        Assert.Single(registry.Descriptors);
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired("missing"));
    }

    [Fact]
    public async Task Intelligence_context_enforces_job_idempotency_key()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.IntelligenceJobs.Add(new IntelligenceJob
        {
            Type = "daily-scan",
            ScopeKey = "instance",
            IdempotencyKey = "daily-scan:2026-09-01"
        });
        await db.SaveChangesAsync();

        db.IntelligenceJobs.Add(new IntelligenceJob
        {
            Type = "daily-scan",
            ScopeKey = "instance",
            IdempotencyKey = "daily-scan:2026-09-01"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Pending_suggestion_semantic_index_allows_history_but_store_can_dedupe_pending_work()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.IntelligenceSuggestions.Add(new IntelligenceSuggestion
        {
            Type = "category",
            SubjectType = "transaction",
            SubjectId = "tx-1",
            SemanticKey = "category:food.groceries",
            Status = IntelligenceSuggestionStatuses.Pending
        });
        await db.SaveChangesAsync();

        var duplicate = await db.IntelligenceSuggestions.AnyAsync(x =>
            x.Status == IntelligenceSuggestionStatuses.Pending &&
            x.SubjectType == "transaction" &&
            x.SubjectId == "tx-1" &&
            x.SemanticKey == "category:food.groceries");

        Assert.True(duplicate);
    }

    private sealed class FakeProvider : IIntelligenceProvider
    {
        public IntelligenceProviderDescriptor Descriptor { get; } = new(
            "fake",
            IntelligenceProviderCapabilities.TextClassification,
            1024,
            ReportsUsage: false);

        public Task<IntelligenceProviderTestResult> TestCredentialAsync(string credential, CancellationToken cancellationToken) =>
            Task.FromResult(new IntelligenceProviderTestResult(true));

        public Task<IntelligenceProviderResponse> ExecuteAsync(IntelligenceProviderRequest request, string credential, CancellationToken cancellationToken) =>
            Task.FromResult(new IntelligenceProviderResponse("{}", null, null, null));
    }
}
