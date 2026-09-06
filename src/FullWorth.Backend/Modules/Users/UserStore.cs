using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Users;

public sealed class UserStore(DbContext db)
{
    public Task<FullWorthUser?> GetAsync(Guid userId, CancellationToken ct) => db.Set<FullWorthUser>()
        .AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == userId, ct);

    public Task<FullWorthUser?> GetByEmailAsync(string email, CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmail(email);
        return db.Set<FullWorthUser>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmailNormalized == normalizedEmail, ct);
    }

    public async Task<FullWorthUser> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        var displayName = NormalizeDisplayName(request.DisplayName);

        if (await db.Set<FullWorthUser>().AnyAsync(x => x.EmailNormalized == normalizedEmail, ct))
            throw new InvalidOperationException("A user with this e-mail already exists.");

        var now = DateTimeOffset.UtcNow;
        var user = new FullWorthUser
        {
            EmailNormalized = normalizedEmail,
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Set<FullWorthUser>().Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<FullWorthUser?> UpdateAsync(Guid userId, UpdateUserRequest request, CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        var displayName = NormalizeDisplayName(request.DisplayName);
        var user = await db.Set<FullWorthUser>().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return null;

        if (await db.Set<FullWorthUser>().AnyAsync(x => x.Id != userId && x.EmailNormalized == normalizedEmail, ct))
            throw new InvalidOperationException("A user with this e-mail already exists.");

        user.EmailNormalized = normalizedEmail;
        user.DisplayName = displayName;
        user.IsActive = request.IsActive;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<bool> SetActiveAsync(Guid userId, bool active, CancellationToken ct)
    {
        var user = await db.Set<FullWorthUser>().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null || user.IsTombstone) return false;
        user.IsActive = active;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TombstoneAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Set<FullWorthUser>().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return false;
        if (user.IsTombstone) return true;

        user.EmailNormalized = $"DELETED-{user.Id:N}@INVALID.FULLWORTH";
        user.DisplayName = "Deleted user";
        user.IsActive = false;
        user.IsTombstone = true;
        user.OnboardingVersion = 0;
        user.OnboardingCompletedAt = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("E-mail is required.", nameof(email));

        var normalized = email.Trim().ToUpperInvariant();
        if (normalized.Length > 320)
            throw new ArgumentException("E-mail must not exceed 320 characters.", nameof(email));

        return normalized;
    }

    private static string NormalizeDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        var normalized = displayName.Trim();
        if (normalized.Length > 200)
            throw new ArgumentException("Display name must not exceed 200 characters.", nameof(displayName));

        return normalized;
    }
}
