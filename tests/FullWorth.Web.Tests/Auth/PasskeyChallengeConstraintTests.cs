using FullWorth.Web.Data;
using FullWorth.Web.Modules.Passkeys;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Auth;

public sealed class PasskeyChallengeConstraintTests
{
    [Fact]
    public async Task PostgreSqlRejectsUnknownChallengeType()
    {
        await AssertRejectedAsync(new PasskeyChallenge
        {
            Id = Guid.NewGuid(),
            Type = (PasskeyChallengeType)99,
            OptionsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
    }

    [Fact]
    public async Task PostgreSqlRejectsNonPositiveChallengeLifetime()
    {
        var now = DateTimeOffset.UtcNow;
        await AssertRejectedAsync(new PasskeyChallenge
        {
            Id = Guid.NewGuid(),
            Type = PasskeyChallengeType.Login,
            OptionsJson = "{}",
            CreatedAt = now,
            ExpiresAt = now
        });
    }

    [Fact]
    public async Task PostgreSqlRejectsConsumedTimestampBeforeCreation()
    {
        var now = DateTimeOffset.UtcNow;
        await AssertRejectedAsync(new PasskeyChallenge
        {
            Id = Guid.NewGuid(),
            Type = PasskeyChallengeType.Login,
            OptionsJson = "{}",
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
            ConsumedAt = now.AddSeconds(-1)
        });
    }

    private static async Task AssertRejectedAsync(PasskeyChallenge challenge)
    {
        await using var database = await PostgresAuthDatabase.CreateAsync();
        await using var services = AuthTestServices.Build(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.MigrateAsync();

        db.PasskeyChallenges.Add(challenge);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
