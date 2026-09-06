using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Passkeys;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FullWorth.Web.Tests.Auth;

public sealed class AuthMigrationTests
{
    private const string InitialMigration = "20260812193000_InitialAuthSchema";
    private const string IntegrationMigration = "20260812202000_SessionsAndRecoveryCodes";
    private const string PasskeyMigration = "20260812235500_Passkeys";
    private const string AccountDeletionMigration = "20260906100000_AccountDeletion";
    private const string AdminUserManagementMigration = "20260906201500_AdminUserManagement";
    private static readonly string[] CurrentMigrations = [InitialMigration, IntegrationMigration, PasskeyMigration, AccountDeletionMigration, AdminUserManagementMigration];

    [Fact]
    public async Task ExistingAuthDatabase_UpgradesThroughAdminUserManagementAndPreservesExistingUser()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(IntegrationMigration);
        Assert.Equal([InitialMigration, IntegrationMigration], (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.False(await TableExistsAsync(database.ConnectionString, "PasskeyCredentials"));
        Assert.False(await TableExistsAsync(database.ConnectionString, "PasskeyChallenges"));

        var legacyUserId = Guid.NewGuid();
        var legacyFinanceUserId = Guid.NewGuid();
        var email = $"wave-c-{Guid.NewGuid():N}@example.com";
        await InsertLegacyUserAsync(database.ConnectionString, legacyUserId, legacyFinanceUserId, email);

        await migrator.MigrateAsync(PasskeyMigration);
        Assert.True(await TableExistsAsync(database.ConnectionString, "PasskeyCredentials"));
        Assert.True(await TableExistsAsync(database.ConnectionString, "PasskeyChallenges"));

        await migrator.MigrateAsync(AccountDeletionMigration);
        await migrator.MigrateAsync(AdminUserManagementMigration);

        Assert.Equal(CurrentMigrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await ColumnExistsAsync(database.ConnectionString, "AspNetUsers", "DeletionScheduledFor"));
        Assert.True(await ColumnExistsAsync(database.ConnectionString, "AspNetUsers", "DeletionRequestedAt"));
        Assert.True(await ColumnExistsAsync(database.ConnectionString, "AspNetUsers", "IsAdmin"));
        Assert.True(await TableExistsAsync(database.ConnectionString, "AdminAuditEvents"));
        db.ChangeTracker.Clear();
        var preserved = await db.Users.AsNoTracking().SingleAsync(x => x.Id == legacyUserId);
        Assert.Equal(legacyFinanceUserId, preserved.FinanceUserId);
        Assert.Equal(email, preserved.Email);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task FreshAuthMigrationCreatesIdentitySessionsRecoveryAndPasskeyTables()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.MigrateAsync();

        Assert.Equal(CurrentMigrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(4, await CountIdentityTablesAsync(database.ConnectionString));
        foreach (var table in new[] { "UserSessions", "RecoveryCodes", "PasskeyCredentials", "PasskeyChallenges", "__EFMigrationsHistory" })
            Assert.True(await TableExistsAsync(database.ConnectionString, table));
        Assert.True(await ColumnExistsAsync(database.ConnectionString, "AspNetUsers", "DeletionScheduledFor"));
        Assert.True(await ColumnExistsAsync(database.ConnectionString, "AspNetUsers", "IsAdmin"));
        Assert.True(await TableExistsAsync(database.ConnectionString, "AdminAuditEvents"));
        Assert.False(await TableExistsAsync(database.ConnectionString, "Accounts"));
        Assert.False(await TableExistsAsync(database.ConnectionString, "Transactions"));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ApplyingAuthMigrationsRepeatedlyDoesNotFailOrDrift()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);

        await using (var firstScope = services.CreateAsyncScope())
        {
            var first = firstScope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await first.Database.MigrateAsync();
            await first.Database.MigrateAsync();
        }

        await using var secondScope = services.CreateAsyncScope();
        var second = secondScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await second.Database.MigrateAsync();

        Assert.Equal(CurrentMigrations, (await second.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.False(second.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task PasskeyCredentialId_IsGloballyUniqueAcrossUsersInPostgres()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.MigrateAsync();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var first = await CreateUserAsync(auth);
        var second = await CreateUserAsync(auth);
        var credentialId = new byte[] { 1, 3, 3, 7, 9 };

        db.PasskeyCredentials.Add(CreateCredential(first, credentialId, "first"));
        await db.SaveChangesAsync();
        db.PasskeyCredentials.Add(CreateCredential(second, credentialId, "second"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task PasskeyCredential_RequiresExistingAuthUserInPostgres()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.MigrateAsync();
        db.PasskeyCredentials.Add(CreateCredential(Guid.NewGuid(), [7, 8, 9], "foreign"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ChallengeConsume_IsAtomicAcrossConcurrentPostgresContexts()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        var challengeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var setupScope = services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.Database.MigrateAsync();
            db.PasskeyChallenges.Add(new PasskeyChallenge
            {
                Id = challengeId,
                Type = PasskeyChallengeType.Login,
                OptionsJson = "{}",
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(5)
            });
            await db.SaveChangesAsync();
        }

        await using var firstScope = services.CreateAsyncScope();
        await using var secondScope = services.CreateAsyncScope();
        var firstStore = new PasskeyChallengeStore(firstScope.ServiceProvider.GetRequiredService<AuthDbContext>());
        var secondStore = new PasskeyChallengeStore(secondScope.ServiceProvider.GetRequiredService<AuthDbContext>());

        var results = await Task.WhenAll(
            firstStore.ConsumeAsync(challengeId, PasskeyChallengeType.Login, null, now.AddSeconds(1)),
            secondStore.ConsumeAsync(challengeId, PasskeyChallengeType.Login, null, now.AddSeconds(1)));

        Assert.Equal(1, results.Count(x => x is not null));
    }

    private static async Task<Guid> CreateUserAsync(AuthService auth)
    {
        var financeUserId = Guid.NewGuid();
        var result = await auth.CreateUserAsync(new CreateAuthUserRequest(
            financeUserId,
            $"passkey-{Guid.NewGuid():N}@example.com",
            "correct horse battery staple"));
        Assert.True(result.Succeeded);
        return Assert.IsType<Guid>(result.User!.Id);
    }

    private static PasskeyCredential CreateCredential(Guid authUserId, byte[] credentialId, string displayName) => new()
    {
        Id = Guid.NewGuid(),
        AuthUserId = authUserId,
        CredentialId = credentialId,
        PublicKey = [4, 5, 6],
        UserHandle = authUserId.ToByteArray(),
        SignatureCounter = 0,
        DisplayName = displayName,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static async Task InsertLegacyUserAsync(
        string connectionString,
        Guid id,
        Guid financeUserId,
        string email)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO auth."AspNetUsers"
            ("Id","FinanceUserId","CreatedAt","UpdatedAt","UserName","NormalizedUserName","Email","NormalizedEmail",
             "EmailConfirmed","PasswordHash","SecurityStamp","ConcurrencyStamp","PhoneNumber","PhoneNumberConfirmed",
             "TwoFactorEnabled","LockoutEnd","LockoutEnabled","AccessFailedCount","IsDisabled")
            VALUES
            (@id,@financeUserId,@createdAt,@updatedAt,@userName,@normalizedUserName,@email,@normalizedEmail,
             false,NULL,@securityStamp,@concurrencyStamp,NULL,false,false,NULL,true,0,false);
            """;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("financeUserId", financeUserId);
        command.Parameters.AddWithValue("createdAt", now);
        command.Parameters.AddWithValue("updatedAt", now);
        command.Parameters.AddWithValue("userName", email);
        command.Parameters.AddWithValue("normalizedUserName", email.ToUpperInvariant());
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("normalizedEmail", email.ToUpperInvariant());
        command.Parameters.AddWithValue("securityStamp", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("concurrencyStamp", Guid.NewGuid().ToString("N"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountIdentityTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'auth' AND table_name LIKE 'AspNet%';";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }


    private static async Task<bool> ColumnExistsAsync(string connectionString, string tableName, string columnName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'auth' AND table_name = @table AND column_name = @column);";
        command.Parameters.AddWithValue("table", tableName);
        command.Parameters.AddWithValue("column", columnName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string tableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'auth' AND table_name = @table);";
        command.Parameters.AddWithValue("table", tableName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
