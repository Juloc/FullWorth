using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class AiInstanceSettingsSingletonTests
{
    [Fact]
    public async Task Get_or_create_reuses_the_single_instance_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new IntelligenceStore(db, FieldCipher.Null,
            new IntelligenceProviderRegistry([new NoopProvider()]));

        var first = await store.GetOrCreateInstanceSettingsAsync(CancellationToken.None);
        var second = await store.GetOrCreateInstanceSettingsAsync(CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(AiInstanceSettings.InstanceScopeKey, first.ScopeKey);
        Assert.Single(await db.AiInstanceSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Database_rejects_duplicate_instance_scope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AiInstanceSettings.Add(new AiInstanceSettings());
        await db.SaveChangesAsync();
        db.AiInstanceSettings.Add(new AiInstanceSettings());

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private sealed class NoopProvider : IIntelligenceProvider
    {
        public IntelligenceProviderDescriptor Descriptor { get; } = new(
            IntelligenceProviders.OpenAi,
            IntelligenceProviderCapabilities.TextClassification,
            1024,
            ReportsUsage: false);

        public Task<IntelligenceProviderTestResult> TestCredentialAsync(string credential, CancellationToken cancellationToken) =>
            Task.FromResult(new IntelligenceProviderTestResult(true));

        public Task<IntelligenceProviderResponse> ExecuteAsync(
            IntelligenceProviderRequest request,
            string credential,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IntelligenceProviderResponse("{}", null, null, null));
    }
}
