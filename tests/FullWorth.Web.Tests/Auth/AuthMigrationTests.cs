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
    private static readonly string[] CurrentMigrations = [InitialMigration, IntegrationMigration, PasskeyMigration];

    [Fact]
    public async Task WaveCAuthDatabase_UpgradesToPasskeysAndPreservesExistingUser()
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

        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var email = $"wave-c-{Guid.NewGuid():N}@example.com";
        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, "correct horse battery staple"));
        Assert.True(created.Succeeded);

        await migrator.MigrateAsync(PasskeyMigration);

        Assert.Equal(CurrentMigrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await TableExistsAsync(database.ConnectionString, "PasskeyCredentials"));
        Assert.True(await TableExistsAsync(database.ConnectionString, "PasskeyChallenges"));
        Assert.True((await auth.ValidatePasswordAsync(email, "correct horse battery staple")).Succeeded);
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

    private static async Task<int> CountIdentityTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'auth' AND table_name LIKE 'AspNet%';";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
