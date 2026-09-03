using System.Security.Cryptography;
using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Recovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Auth;

public sealed class RecoveryPersistenceTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task ThirtyTwoParallelConsumes_AllowExactlyOneSuccess()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        Guid authUserId;
        string code;

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.Database.MigrateAsync();
            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
            var created = await auth.CreateUserAsync(new CreateAuthUserRequest(
                Guid.NewGuid(),
                $"recovery-{Guid.NewGuid():N}@example.com",
                Password));
            Assert.True(created.Succeeded);
            authUserId = created.User!.Id;

            var recovery = scope.ServiceProvider.GetRequiredService<RecoveryService>();
            var generated = await recovery.GenerateAsync(authUserId);
            Assert.Equal(RecoveryOptions.DefaultCodeCount, generated.Codes.Count);
            code = generated.Codes[0];
        }

        var attempts = Enumerable.Range(0, 32).Select(async _ =>
        {
            await using var scope = services.CreateAsyncScope();
            var recovery = scope.ServiceProvider.GetRequiredService<RecoveryService>();
            return await recovery.ValidateAndConsumeAsync(authUserId, code);
        });

        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(x => x));
        Assert.Equal(31, results.Count(x => !x));
    }

    [Fact]
    public async Task FailedRegeneration_RollsBackAndLeavesPreviousSetActive()
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        Guid authUserId;
        string previousCode;

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.Database.MigrateAsync();
            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
            var created = await auth.CreateUserAsync(new CreateAuthUserRequest(
                Guid.NewGuid(),
                $"regen-{Guid.NewGuid():N}@example.com",
                Password));
            Assert.True(created.Succeeded);
            authUserId = created.User!.Id;

            var recovery = scope.ServiceProvider.GetRequiredService<RecoveryService>();
            previousCode = (await recovery.GenerateAsync(authUserId)).Codes[0];
        }

        await using (var failingScope = services.CreateAsyncScope())
        {
            var store = failingScope.ServiceProvider.GetRequiredService<IRecoveryCodeStore>();
            var duplicateHash = RandomNumberGenerator.GetBytes(32);
            var now = DateTimeOffset.UtcNow;
            var invalidReplacement = new[]
            {
                new RecoveryCode { Id = Guid.NewGuid(), AuthUserId = authUserId, CodeHash = duplicateHash, CreatedAt = now },
                new RecoveryCode { Id = Guid.NewGuid(), AuthUserId = authUserId, CodeHash = duplicateHash, CreatedAt = now }
            };

            await Assert.ThrowsAsync<DbUpdateException>(() => store.ReplaceAsync(authUserId, invalidReplacement));
        }

        await using var verifyScope = services.CreateAsyncScope();
        var verifyRecovery = verifyScope.ServiceProvider.GetRequiredService<RecoveryService>();
        Assert.True(await verifyRecovery.ValidateAndConsumeAsync(authUserId, previousCode));
    }
}
