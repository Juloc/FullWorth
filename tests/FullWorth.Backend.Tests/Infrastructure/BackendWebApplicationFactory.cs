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
using Npgsql;

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
                 descriptor.ImplementationType == typeof(IntelligenceScheduledJobWorker))).ToList())
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

    // Migrating the full schema (~114 EF migrations) into a fresh database for each of the 100+ test
    // classes dominated the suite runtime. Instead we apply the migrations ONCE into a template database
    // and clone it per class with `CREATE DATABASE ... TEMPLATE` (a fast file-level copy). The app's
    // start-up MigrateAsync then finds every migration already applied and no-ops.
    private const string TemplateDatabaseName = "fullworth_test_template_backend";
    private static readonly Lazy<string> TemplateDatabase = new(BuildTemplateDatabase);

    private static string CreateConnectionString()
    {
        var server = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException("FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server.");

        var baseConnection = server.TrimEnd(';');
        var database = $"fullworth_test_{Guid.NewGuid():N}";
        // Escape hatch (A/B benchmarking / fallback): FULLWORTH_TEST_NO_TEMPLATE=1 keeps the original
        // behaviour where each class's database is created and migrated from scratch by the app start-up.
        if (Environment.GetEnvironmentVariable("FULLWORTH_TEST_NO_TEMPLATE") != "1")
            CloneDatabase(baseConnection, TemplateDatabase.Value, database);

        // Each test class gets its own database and connection pool. With the full suite that is
        // hundreds of pools; a large pool size plus the default 300s idle lifetime lets connections
        // accumulate past the server's max_connections ("too many clients already"). Keep pools small
        // and prune idle connections quickly so the whole suite stays well within the connection budget.
        return $"{baseConnection};Database={database};Maximum Pool Size=10;Minimum Pool Size=0;Connection Idle Lifetime=5;Connection Pruning Interval=2";
    }

    private static string BuildTemplateDatabase()
    {
        var baseConnection = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES")!.TrimEnd(';');
        // Fixed name, rebuilt from scratch each run so a crashed previous run cannot leave a stale schema.
        ExecuteMaintenance(baseConnection, $"DROP DATABASE IF EXISTS \"{TemplateDatabaseName}\" WITH (FORCE)");
        ExecuteMaintenance(baseConnection, $"CREATE DATABASE \"{TemplateDatabaseName}\"");

        // Start a real backend against the template so its schema, migration history and seed data are
        // exactly what a freshly started backend produces — clones are then indistinguishable from a
        // database this factory would previously have migrated in place.
        using (var factory = new BackendWebApplicationFactory($"{baseConnection};Database={TemplateDatabaseName}"))
        using (factory.CreateClient())
        {
        }

        // A TEMPLATE source must have zero live sessions. Release the pool the start-up host used and
        // terminate any stragglers before the first clone runs.
        NpgsqlConnection.ClearAllPools();
        ExecuteMaintenance(baseConnection,
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{TemplateDatabaseName}' AND pid <> pg_backend_pid()");
        return TemplateDatabaseName;
    }

    private static void CloneDatabase(string baseConnection, string template, string database)
    {
        // Concurrent clones from the same template are fine (no session connects to the template), but a
        // just-released pooled connection can momentarily linger; retry briefly on the transient
        // "source database is being accessed by other users".
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ExecuteMaintenance(baseConnection, $"CREATE DATABASE \"{database}\" TEMPLATE \"{template}\"");
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 5)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void ExecuteMaintenance(string baseConnection, string sql)
    {
        using var connection = new NpgsqlConnection($"{baseConnection};Database=fullworth_test;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
