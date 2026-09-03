using FullWorth.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Passkeys;

public sealed class PasskeyChallengeCleanup(AuthDbContext db, TimeProvider clock)
{
    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return db.PasskeyChallenges
            .Where(x => x.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
