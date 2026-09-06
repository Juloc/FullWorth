using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Bootstrap;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Admin;

public static class InstanceAdminBootstrapper
{
    public static async Task EnsureAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var db = services.GetRequiredService<AuthDbContext>();
        if (await db.Users.AsNoTracking().AnyAsync(x => x.IsAdmin, ct))
            return;

        var usersExist = await db.Users.AsNoTracking().AnyAsync(ct);
        if (!usersExist)
            return;

        AuthUser? candidate = null;
        var options = services.GetRequiredService<IConfiguration>()
            .GetSection(BootstrapOptions.SectionName)
            .Get<BootstrapOptions>();

        if (!string.IsNullOrWhiteSpace(options?.Email))
        {
            var normalized = options.Email.Trim().ToUpperInvariant();
            candidate = await db.Users.FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
        }

        candidate ??= await db.Users
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstAsync(ct);

        candidate.IsAdmin = true;
        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "No instance admin existed. Granted instance admin to auth user {AuthUserId}.",
            candidate.Id);
    }
}
