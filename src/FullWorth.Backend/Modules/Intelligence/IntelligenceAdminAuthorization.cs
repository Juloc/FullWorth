using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed class IntelligenceAdminGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public bool IsBootstrapAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class IntelligenceAdminModelConfiguration
{
    public static void ConfigureAdmin(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntelligenceAdminGrant>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UserId).IsUnique();
        });
    }
}

public sealed class IntelligenceAdminBootstrapper(
    FullWorthDbContext financeDb,
    IntelligenceDbContext intelligenceDb)
{
    public async Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken)
    {
        if (await intelligenceDb.IntelligenceAdminGrants.AsNoTracking().AnyAsync(cancellationToken))
            return;

        var firstUserId = await financeDb.Users.AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (firstUserId == Guid.Empty)
            return;

        intelligenceDb.IntelligenceAdminGrants.Add(new IntelligenceAdminGrant
        {
            UserId = firstUserId,
            IsBootstrapAdmin = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await intelligenceDb.SaveChangesAsync(cancellationToken);
    }
}

public sealed class IntelligenceAdminAuthorizer(IntelligenceDbContext db)
{
    public Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken) =>
        db.IntelligenceAdminGrants.AsNoTracking().AnyAsync(x => x.UserId == userId, cancellationToken);
}
