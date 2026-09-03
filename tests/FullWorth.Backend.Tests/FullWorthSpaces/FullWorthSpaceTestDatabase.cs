using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.FullWorthSpaces;

internal sealed class FullWorthSpaceTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly DbContextOptions<FullWorthSpaceTestContext> options;

    private FullWorthSpaceTestDatabase(SqliteConnection connection)
    {
        this.connection = connection;
        options = new DbContextOptionsBuilder<FullWorthSpaceTestContext>()
            .UseSqlite(connection)
            .Options;
    }

    public static async Task<FullWorthSpaceTestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = new FullWorthSpaceTestDatabase(connection);
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        return database;
    }

    public FullWorthSpaceTestContext CreateContext() => new(options);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

internal sealed class FullWorthSpaceTestContext(DbContextOptions<FullWorthSpaceTestContext> options) : DbContext(options)
{
    public DbSet<FullWorthSpace> FullWorthSpaces => Set<FullWorthSpace>();
    public DbSet<FullWorthSpaceMember> FullWorthSpaceMembers => Set<FullWorthSpaceMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FullWorthSpace>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.BaseCurrency).IsRequired().HasMaxLength(3);
            entity.HasMany(x => x.Members)
                .WithOne(x => x.FullWorthSpace)
                .HasForeignKey(x => x.FullWorthSpaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FullWorthSpaceMember>(entity =>
        {
            entity.HasKey(x => new { x.FullWorthSpaceId, x.UserId });
            entity.HasIndex(x => x.UserId);
            entity.HasIndex(x => new { x.FullWorthSpaceId, x.Role });
            entity.Property(x => x.Role).IsRequired().HasMaxLength(16);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_FullWorthSpaceMembers_Role",
                "Role IN ('owner', 'member')"));
        });
    }
}
