using System.Reflection;
using FullWorth.Backend.Modules.Users;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Users;

public sealed class UserStoreTests
{
    [Fact]
    public async Task CreateUserPersistsIdentityMetadata()
    {
        await using var database = await UsersSqliteDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new UserStore(db);

        var user = await store.CreateAsync(new CreateUserRequest("alice@example.com", " Alice "), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("ALICE@EXAMPLE.COM", user.EmailNormalized);
        Assert.Equal("Alice", user.DisplayName);
        Assert.True(user.IsActive);
        Assert.Equal(user.CreatedAt, user.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, user.CreatedAt.Offset);
    }

    [Fact]
    public async Task GetUserByIdReturnsCreatedUser()
    {
        await using var database = await UsersSqliteDatabase.CreateAsync();
        Guid userId;

        await using (var db = database.CreateContext())
        {
            var store = new UserStore(db);
            userId = (await store.CreateAsync(new CreateUserRequest("alice@example.com", "Alice"), CancellationToken.None)).Id;
        }

        await using var verification = database.CreateContext();
        var found = await new UserStore(verification).GetAsync(userId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(userId, found.Id);
        Assert.Equal("Alice", found.DisplayName);
    }

    [Fact]
    public async Task GetByEmailAcceptsEquivalentNormalizedEmail()
    {
        await using var database = await UsersSqliteDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new UserStore(db);
        var created = await store.CreateAsync(new CreateUserRequest("alice@example.com", "Alice"), CancellationToken.None);

        var found = await store.GetByEmailAsync("  ALIce@Example.Com  ", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
    }

    [Fact]
    public async Task EmailNormalizationTrimsAndUsesInvariantUppercase()
    {
        await using var database = await UsersSqliteDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new UserStore(db);

        var user = await store.CreateAsync(new CreateUserRequest("  Alice.Smith+Test@Example.COM  ", "Alice"), CancellationToken.None);

        Assert.Equal("ALICE.SMITH+TEST@EXAMPLE.COM", user.EmailNormalized);
    }

    [Fact]
    public async Task UpdateChangesDisplayName()
    {
        await using var database = await UsersSqliteDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new UserStore(db);
        var created = await store.CreateAsync(new CreateUserRequest("alice@example.com", "Alice"), CancellationToken.None);
        var createdAt = created.CreatedAt;

        var updated = await store.UpdateAsync(
            created.Id,
            new UpdateUserRequest("alice@example.com", " Alice Updated ", true),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Alice Updated", updated.DisplayName);
        Assert.Equal(createdAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= createdAt);
    }

    [Fact]
    public async Task UpdateCanDisableUserWithoutDeletingIt()
    {
        await using var database = await UsersSqliteDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new UserStore(db);
        var created = await store.CreateAsync(new CreateUserRequest("alice@example.com", "Alice"), CancellationToken.None);

        var updated = await store.UpdateAsync(
            created.Id,
            new UpdateUserRequest("alice@example.com", "Alice", false),
            CancellationToken.None);
        var persisted = await store.GetAsync(created.Id, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.False(updated.IsActive);
        Assert.NotNull(persisted);
        Assert.False(persisted.IsActive);
    }

    [Fact]
    public async Task DuplicateNormalizedEmailIsRejected()
    {
        await using var database = await UsersSqliteDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var store = new UserStore(db);
        await store.CreateAsync(new CreateUserRequest("alice@example.com", "Alice"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(new CreateUserRequest("  ALICE@EXAMPLE.COM  ", "Other Alice"), CancellationToken.None));
    }

    [Fact]
    public void FullWorthUserContainsNoAuthenticationCredentialFields()
    {
        var propertyNames = typeof(FullWorthUser)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(
            new[] { "CreatedAt", "DisplayName", "Email", "EmailNormalized", "Id", "IsActive", "UpdatedAt" },
            propertyNames);
    }
}

internal sealed class UsersSqliteDatabase : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly DbContextOptions<UsersDbContext> options;

    private UsersSqliteDatabase(SqliteConnection connection)
    {
        this.connection = connection;
        options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseSqlite(connection)
            .Options;
    }

    public static async Task<UsersSqliteDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var database = new UsersSqliteDatabase(connection);
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        return database;
    }

    public UsersDbContext CreateContext() => new(options);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

internal sealed class UsersDbContext(DbContextOptions<UsersDbContext> options) : DbContext(options)
{
    public DbSet<FullWorthUser> Users => Set<FullWorthUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new FullWorthUserConfiguration());
    }
}
