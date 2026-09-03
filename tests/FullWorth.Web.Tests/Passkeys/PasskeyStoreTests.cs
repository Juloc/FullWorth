using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Passkeys;
using FullWorth.Web.Tests.Auth;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Tests.Passkeys;

public sealed class PasskeyStorePostgresFixture : IAsyncLifetime
{
    public PostgresAuthDatabase Database { get; private set; } = null!;
    public DbContextOptions<PasskeyStoreTestDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await PostgresAuthDatabase.CreateAsync();
        Options = new DbContextOptionsBuilder<PasskeyStoreTestDbContext>()
            .UseNpgsql(Database.ConnectionString)
            .Options;
        await using var db = new PasskeyStoreTestDbContext(Options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await Database.DisposeAsync();

    public async Task<Guid> AddUserAsync()
    {
        var id = Guid.NewGuid();
        await using var db = new PasskeyStoreTestDbContext(Options);
        db.Users.Add(new AuthUser
        {
            Id = id,
            UserName = $"user-{id:N}",
            NormalizedUserName = $"USER-{id:N}",
            Email = $"{id:N}@example.invalid",
            NormalizedEmail = $"{id:N}@EXAMPLE.INVALID",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }
}

public sealed class PasskeyStoreTestDbContext(DbContextOptions<PasskeyStoreTestDbContext> options) : DbContext(options)
{
    public DbSet<AuthUser> Users => Set<AuthUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthUser>().ToTable("PasskeyTestUsers", "auth");
        modelBuilder.ConfigurePasskeys();
    }
}

public sealed class PasskeyStoreTests(PasskeyStorePostgresFixture fixture) : IClassFixture<PasskeyStorePostgresFixture>
{
    [Fact]
    public async Task Credential_id_unique_index_prevents_cross_user_duplicate()
    {
        var firstUser = await fixture.AddUserAsync();
        var secondUser = await fixture.AddUserAsync();
        var id = new byte[] { 10, 20, 30 };

        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
        {
            var store = new PasskeyStore(db);
            await store.CreateAsync(Credential(firstUser, id));
        }

        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
        {
            var store = new PasskeyStore(db);
            await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(Credential(secondUser, id)));
        }
    }

    [Fact]
    public async Task Concrete_store_list_and_delete_are_user_scoped()
    {
        var owner = await fixture.AddUserAsync();
        var other = await fixture.AddUserAsync();
        var credential = Credential(owner, [11, 22, 33]);
        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
            await new PasskeyStore(db).CreateAsync(credential);

        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
        {
            var store = new PasskeyStore(db);
            Assert.Empty(await store.ListAsync(other));
            Assert.False(await store.DeleteAsync(other, credential.Id));
            Assert.True(await store.DeleteAsync(owner, credential.Id));
        }
    }

    [Fact]
    public async Task Concrete_store_updates_assertion_state_only_from_expected_counter()
    {
        var owner = await fixture.AddUserAsync();
        var credential = Credential(owner, [44, 55, 66]);
        credential.SignatureCounter = 7;
        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
            await new PasskeyStore(db).CreateAsync(credential);

        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
        {
            var store = new PasskeyStore(db);
            Assert.False(await store.UpdateAfterAssertionAsync(owner, credential.CredentialId, 6, 8, false, DateTimeOffset.UtcNow));
            Assert.True(await store.UpdateAfterAssertionAsync(owner, credential.CredentialId, 7, 8, true, DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task Challenge_consume_is_atomic_under_concurrency()
    {
        var challenge = new PasskeyChallenge
        {
            Id = Guid.NewGuid(),
            Type = PasskeyChallengeType.Login,
            OptionsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
            await new PasskeyChallengeStore(db).CreateAsync(challenge);

        var now = DateTimeOffset.UtcNow;
        async Task<PasskeyChallenge?> ConsumeAsync()
        {
            await using var db = new PasskeyStoreTestDbContext(fixture.Options);
            return await new PasskeyChallengeStore(db).ConsumeAsync(challenge.Id, PasskeyChallengeType.Login, null, now);
        }

        var results = await Task.WhenAll(ConsumeAsync(), ConsumeAsync());
        Assert.Single(results, x => x is not null);
    }

    [Fact]
    public async Task Concrete_challenge_store_rejects_expired_challenge()
    {
        var challenge = new PasskeyChallenge
        {
            Id = Guid.NewGuid(),
            Type = PasskeyChallengeType.Login,
            OptionsJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
            await new PasskeyChallengeStore(db).CreateAsync(challenge);

        await using (var db = new PasskeyStoreTestDbContext(fixture.Options))
        {
            var consumed = await new PasskeyChallengeStore(db).ConsumeAsync(
                challenge.Id,
                PasskeyChallengeType.Login,
                null,
                DateTimeOffset.UtcNow);
            Assert.Null(consumed);
        }
    }

    private static PasskeyCredential Credential(Guid authUserId, byte[] credentialId) => new()
    {
        Id = Guid.NewGuid(),
        AuthUserId = authUserId,
        CredentialId = credentialId,
        PublicKey = [1, 2, 3],
        UserHandle = PasskeyService.CreateUserHandle(authUserId),
        DisplayName = "Test passkey",
        CreatedAt = DateTimeOffset.UtcNow
    };
}
