using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Coach;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Backend.Tests.Infrastructure;

internal sealed class BackendWebApplicationFactory : WebApplicationFactory<FullWorthDbContext>
{
    public const string InternalKey = "backend-test-internal-key-7f496f1e2a4b46c8";
    public const string IngestKey = "backend-test-ingest-key";
    public const string ReadKey = "backend-test-read-key";
    public const string WriteKey = "backend-test-write-key";

    private readonly string connectionString;
    private readonly IReadOnlyDictionary<string, string?> configurationOverrides;
    private readonly Action<IServiceCollection>? serviceOverrides;
    private readonly string purchaseStorageRoot = Path.Combine(Path.GetTempPath(), "fullworth-purchase-tests", Guid.NewGuid().ToString("N"));

    public BackendWebApplicationFactory()
        : this(CreateConnectionString(), null, null)
    {
    }

    public BackendWebApplicationFactory(string connectionString)
        : this(connectionString, null, null)
    {
    }

    public BackendWebApplicationFactory(IReadOnlyDictionary<string, string?> configurationOverrides)
        : this(CreateConnectionString(), configurationOverrides, null)
    {
    }

    public BackendWebApplicationFactory(
        IReadOnlyDictionary<string, string?> configurationOverrides,
        Action<IServiceCollection> serviceOverrides)
        : this(CreateConnectionString(), configurationOverrides, serviceOverrides)
    {
    }

    public BackendWebApplicationFactory(
        string connectionString,
        IReadOnlyDictionary<string, string?>? configurationOverrides,
        Action<IServiceCollection>? serviceOverrides = null)
    {
        this.connectionString = connectionString;
        this.configurationOverrides = configurationOverrides ?? new Dictionary<string, string?>();
        this.serviceOverrides = serviceOverrides;
    }

    public string ConnectionString => connectionString;
    public string PurchaseStorageRoot => purchaseStorageRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Security:InternalKey"] = InternalKey,
                ["Security:IngestKey"] = IngestKey,
                ["PurchaseStorage:RootPath"] = purchaseStorageRoot
            };
            foreach (var pair in configurationOverrides) values[pair.Key] = pair.Value;
            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<FullWorthDbContext>>();
            services.RemoveAll<DbContextOptions<FullWorthDbContext>>();
            services.RemoveAll<FullWorthDbContext>();
            services.AddDbContext<FullWorthDbContext>(options =>
                options.UseNpgsql(connectionString)
                    .ReplaceService<IModelCustomizer, CoachModelCustomizer>());

            // IntelligenceDbContext shares the "Finance" connection string in production; the app
            // otherwise falls back to the appsettings docker host (fullworth-postgres) which is
            // unresolvable in tests. Point it at the same isolated test database.
            services.RemoveAll<IDbContextOptionsConfiguration<IntelligenceDbContext>>();
            services.RemoveAll<DbContextOptions<IntelligenceDbContext>>();
            services.RemoveAll<IntelligenceDbContext>();
            services.AddDbContext<IntelligenceDbContext>(options =>
                options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(IntelligenceDbContext.MigrationHistoryTable)));

            foreach (var worker in services.Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                (descriptor.ImplementationType == typeof(NetWorthSnapshotWorker) ||
                 descriptor.ImplementationType == typeof(FullWorth.Backend.Modules.Fx.FxRateFetchWorker) ||
                 descriptor.ImplementationType == typeof(FullWorth.Backend.Modules.Notifications.ContractDueNotificationWorker) ||
                 descriptor.ImplementationType == typeof(FullWorth.Backend.Modules.Notifications.PropertyAssetNotificationWorker) ||
                 descriptor.ImplementationType == typeof(FullWorth.Backend.Modules.Notifications.PurchaseNotificationWorker) ||
                 descriptor.ImplementationType == typeof(FullWorth.Backend.Modules.Purchases.Amazon.AmazonSyncWorker) ||
                 descriptor.ImplementationType == typeof(FullWorth.Backend.Modules.Purchases.ReceiptScanQueueWorker) ||
                 descriptor.ImplementationType == typeof(FullWorth.Backend.Modules.Tax.TaxAutomaticAnalysisWorker) ||
                 descriptor.ImplementationType == typeof(IntelligenceSchedulePlannerService) ||
                 descriptor.ImplementationType == typeof(IntelligenceScheduledJobWorker) ||
                 descriptor.ImplementationType == typeof(CloudOutboxUploaderWorker) ||
                 descriptor.ImplementationType == typeof(KnowledgePackSyncWorker))).ToList())
                services.Remove(worker);

            serviceOverrides?.Invoke(services);
        });
    }

    public async Task SeedAsync(Func<FullWorthDbContext, Task> seed)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        await seed(db);
    }

    public Task SeedFullWorthUserAsync(Guid userId, bool isActive = true) => SeedAsync(async db =>
    {
        db.Set<FullWorthUser>().Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = $"{userId:N}@EXAMPLE.COM",
            DisplayName = $"Test {userId:N}",
            IsActive = isActive
        });
        await db.SaveChangesAsync();
    });

    private static string CreateConnectionString()
    {
        var server = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException("FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server.");

        // Each test class gets its own database and connection pool. With the full suite that is
        // hundreds of pools; a large pool size plus the default 300s idle lifetime lets connections
        // accumulate past the server's max_connections ("too many clients already"). Keep pools small
        // and prune idle connections quickly so the whole suite stays well within the connection budget.
        return $"{server.TrimEnd(';')};Database=fullworth_test_{Guid.NewGuid():N};Maximum Pool Size=10;Minimum Pool Size=0;Connection Idle Lifetime=5;Connection Pruning Interval=2";
    }
}
