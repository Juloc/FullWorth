using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Recovery;
using FullWorth.Web.Modules.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FullWorth.Web.Tests.Auth;

internal static class AuthTestServices
{
    public static ServiceProvider Build(string connectionString)
    {
        var services = new ServiceCollection();
        Configure(services, connectionString);
        return services.BuildServiceProvider();
    }

    public static void Configure(IServiceCollection services, string connectionString)
    {
        services.AddLogging();
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        }).AddIdentityCookies();
        services.AddAuthorization();

        services.AddDbContext<AuthDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "auth")));

        services.AddIdentityCore<AuthUser>(options => new AuthOptions().Apply(options))
            .AddSignInManager()
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddDefaultTokenProviders();

        services.Configure<SessionOptions>(_ => { });
        services.Configure<RecoveryOptions>(_ => { });
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuthService>();
        services.AddScoped<AuthSessionCoordinator>();
        services.AddScoped<ISessionPersistence, AuthSessionPersistence>();
        services.AddScoped<SessionStore>();
        services.AddScoped<SessionService>();
        services.AddScoped<IRecoveryCodeStore, AuthRecoveryCodeStore>();
        services.AddScoped<IRecoveryUserValidator, AuthRecoveryUserValidator>();
        services.AddScoped<RecoveryService>();
    }
}

public sealed class AuthPostgresFixture : IAsyncLifetime
{
    public PostgresAuthDatabase Database { get; private set; } = null!;
    public ServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await PostgresAuthDatabase.CreateAsync();
        Services = AuthTestServices.Build(Database.ConnectionString);
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Services is not null)
            await Services.DisposeAsync();
        if (Database is not null)
            await Database.DisposeAsync();
    }
}

public sealed class PostgresAuthDatabase : IAsyncDisposable
{
    private readonly string adminConnectionString;
    private readonly string databaseName;

    private PostgresAuthDatabase(string adminConnectionString, string databaseName, string connectionString)
    {
        this.adminConnectionString = adminConnectionString;
        this.databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<PostgresAuthDatabase> CreateAsync()
    {
        var server = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException("FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server.");

        var databaseName = $"fullworth_auth_test_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", MaxPoolSize = 5, MinPoolSize = 0 };
        // Bound per-test connections so the whole suite stays under the CI Postgres max_connections
        // ceiling (parallel test contexts each own a pool). The atomic single-use tests still hold.
        var databaseBuilder = new NpgsqlConnectionStringBuilder(server) { Database = databaseName, MaxPoolSize = 50, MinPoolSize = 0 };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();

        return new PostgresAuthDatabase(adminBuilder.ConnectionString, databaseName, databaseBuilder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        await using (var terminate = connection.CreateCommand())
        {
            terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid();";
            terminate.Parameters.AddWithValue("database", databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
        await drop.ExecuteNonQueryAsync();
    }
}
