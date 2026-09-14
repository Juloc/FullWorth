using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Users;

/// <summary>Wie weit ist dieser Benutzer mit der Einrichtung?</summary>
public sealed record OnboardingState(int Version, DateTimeOffset? CompletedAt);

public sealed class UserOnboardingStore(FullWorthDbContext db)
{
    public Task<OnboardingState?> ReadAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new OnboardingState(user.OnboardingVersion, user.OnboardingCompletedAt))
            .SingleOrDefaultAsync(ct);

    /// <summary>Haelt fest, dass er durch ist - mit der Version, die er gesehen hat.</summary>
    public async Task<OnboardingState?> CompleteAsync(Guid userId, int version, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(row => row.Id == userId, ct);
        if (user is null) return null;

        var now = DateTimeOffset.UtcNow;
        user.OnboardingVersion = version;
        user.OnboardingCompletedAt = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new OnboardingState(version, now);
    }
}
